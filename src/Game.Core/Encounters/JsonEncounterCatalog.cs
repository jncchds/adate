using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Game.Core.Scenes;
using Game.Core.Settings;
using Game.Core.Story;

namespace Game.Core.Encounters;

/// <summary>
/// Loads encounters from <c>{directory}/common.json</c> and <c>{directory}/{setting}.json</c>, adds
/// one encounter per dated setting event, and validates every setting's set at construction.
/// </summary>
/// <remarks>
/// Encounters are references to places, flags and days. A broken one would surface as a beat that
/// silently never fires, which is invisible in play, so each is refused at load with its reference
/// named. A file that matches no setting is refused too: a typo in a file name would otherwise
/// drop a setting's encounters without a word.
/// </remarks>
public sealed partial class JsonEncounterCatalog : IEncounterCatalog
{
    public const string CommonFileName = "common";

    /// <summary>Priority given to dated setting events, above any authored encounter's default.</summary>
    public const int EventPriority = 100;

    /// <summary>Priority of the opening's beats: above ordinary encounters, below events.</summary>
    public const int OpeningPriority = 90;

    /// <summary>
    /// A turn with no encounter where someone's schedule puts them at the place (phase-3 plan: every
    /// turn is written). Not a catalog entry: the world service sets it on the outcome.
    /// </summary>
    public const string QuietCompanyId = "quiet.company";

    /// <summary>A turn with no encounter and nobody the player knows at the place.</summary>
    public const string QuietAloneId = "quiet.alone";

    /// <summary>A turn where someone came looking for the player, of their own accord.</summary>
    public const string InitiativeId = "initiative.visit";

    /// <summary>A turn at the place and time the player agreed to meet someone.</summary>
    public const string PromisedMeetingId = "promise.meet";

    /// <summary>The first date with the main LI, whichever opening led to it.</summary>
    public const string FirstDateId = "beat.first-date";

    /// <summary>The first day a first date can happen (plan §1: week 2 opens the relationship).</summary>
    public const int FirstDateDay = 5;

    /// <summary>The words an encounter's text may ask to have filled in.</summary>
    public static readonly IReadOnlyList<string> TextTokens = ["main_li", "player", "place", "slot", "who", "want", "need"];

    /// <summary>Priority of arc beats: above route beats, below the opening's.</summary>
    public const int ArcPriority = 75;

    /// <summary>The first day a want can be revealed, and the first day its obstacle can appear.</summary>
    public const int ArcRevealDay = 6;

    public const int ArcObstacleDay = 9;

    /// <summary>Who an encounter is with: the main LI, or a variant by its route.</summary>
    public const string MainLiRef = "main_li";

    public const string VariantPrefix = "variant:";

    /// <summary>The first date with the main LI (key <c>main_li</c>) or with the variant on a route.</summary>
    public static string FirstDateIdFor(string key) => key == MainLiRef ? FirstDateId : $"{FirstDateId}.{key}";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IReadOnlyDictionary<string, IReadOnlyList<EncounterDefinition>> _bySetting;

    /// <param name="routes">
    /// The cast's route ids. Each gets generated meeting, contact and first-date beats, and an
    /// encounter with <c>variant:{route}</c> must name one of them. Null skips both.
    /// </param>
    public JsonEncounterCatalog(string directory, ISettingCatalog settings, IReadOnlyList<string>? routes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(settings);

        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Encounters directory '{directory}' not found.");
        }

        var known = settings.All().Select(s => s.Id).Append(CommonFileName).ToHashSet(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!known.Contains(name))
            {
                throw new InvalidOperationException(
                    $"Encounter file '{file}' matches no setting. Name it after a setting id or '{CommonFileName}'.");
            }
        }

        var common = Read(Path.Combine(directory, CommonFileName + ".json"));
        var bySetting = new Dictionary<string, IReadOnlyList<EncounterDefinition>>(StringComparer.Ordinal);

        foreach (var setting in settings.All())
        {
            IReadOnlyList<EncounterDefinition> all =
            [
                .. common,
                .. Read(Path.Combine(directory, setting.Id + ".json")),
                .. setting.Events.Select(Event),
                .. OpeningBeats(setting),
                .. RouteBeats(setting, routes ?? []),
                .. ArcBeats(setting, routes ?? []),
            ];

            Validate(setting, all, routes);
            bySetting[setting.Id] = all;
        }

        _bySetting = bySetting;
    }

    public IReadOnlyList<EncounterDefinition> For(string settingId) =>
        _bySetting.TryGetValue(settingId, out var encounters)
            ? encounters
            : throw new KeyNotFoundException($"No encounters are loaded for setting '{settingId}'.");

    private static EncounterDefinition Event(SettingEvent ev) => new(
        $"event.{ev.Id}",
        new EncounterPlace(Id: ev.Place),
        Time: [ev.Time],
        Days: [ev.Day, ev.Day],
        Priority: EventPriority,
        Text: $"{ev.Name} is happening here today.");

    /// <summary>
    /// The four beats of meeting the main LI (plan §4), the same shape for every opening so the
    /// systems under them are tested once: meet, recognise at the home place (re-armed once at a
    /// second place), a contact choice inside the recognise scene, and a first date from day 5 that
    /// the player invites the main LI to.
    /// </summary>
    private static IEnumerable<EncounterDefinition> OpeningBeats(SettingDefinition setting)
    {
        EncounterChoice[] contact =
        [
            new("swap-numbers", "Ask for their number", ["main_li.contact"], ["adventure"]),
            new("let-it-go", "Let the moment pass", ["main_li.contact_declined"], ["independence"]),
        ];

        foreach (var opening in setting.Openings)
        {
            var chosen = $"opening={opening.Id}";

            yield return new EncounterDefinition(
                $"opening.{opening.Id}.meet",
                new EncounterPlace(Id: opening.MeetingPlace),
                Days: [1, 2],
                Requires: [chosen, "!main_li.met"],
                Sets: ["main_li.met"],
                With: ["main_li"],
                Priority: OpeningPriority,
                Text: $"{opening.Hook} That is how you meet {{main_li}}.");

            yield return new EncounterDefinition(
                $"opening.{opening.Id}.recognise",
                new EncounterPlace(Id: opening.HomePlace),
                Time: [opening.Time],
                Days: [2, 4],
                Requires: [chosen, "main_li.met", "!main_li.recognised"],
                Sets: ["main_li.recognised"],
                With: ["main_li"],
                Priority: OpeningPriority - 5,
                Text: $"{{main_li}} is at {{place}} again, {opening.HomeWindow}, just as they said, and they recognise you straight away.",
                Choices: contact);

            if (opening.SecondPlace is { } second)
            {
                yield return new EncounterDefinition(
                    $"opening.{opening.Id}.recognise-late",
                    new EncounterPlace(Id: second),
                    Days: [5, 7],
                    Requires: [chosen, "main_li.met", "!main_li.recognised"],
                    Sets: ["main_li.recognised", "main_li.recognised_late"],
                    With: ["main_li"],
                    Priority: OpeningPriority - 5,
                    Text: "{main_li} is at {place}, one of the places they mentioned. It takes them a second, and then they smile.",
                    Choices: contact);
            }
        }

        if (setting.Openings.Count > 0)
        {
            yield return new EncounterDefinition(
                FirstDateId,
                new EncounterPlace(),
                Days: [FirstDateDay, setting.Days],
                Requires: ["main_li.contact", "!main_li.first_date", $"{EncounterEvaluator.InviteKey}=main_li"],
                Sets: ["main_li.first_date"],
                With: ["main_li"],
                Priority: OpeningPriority + 5,
                Text: "{main_li} is already at {place} for the {slot} the two of you agreed on. Neither of you calls it a date, and both of you know it is one.");
        }
    }

    /// <summary>
    /// How the player meets and gets close to each variant (plan §5), generated per route so a
    /// setting only authors what is specific to it: where the chance meeting happens. Routine meets
    /// at the routine place after two solo visits; introduced meets on an evening out with the main
    /// LI once they are dating. Every route then has a contact choice where they were met, and a
    /// first date the player invites them to.
    /// </summary>
    private static IEnumerable<EncounterDefinition> RouteBeats(SettingDefinition setting, IReadOnlyList<string> routes)
    {
        foreach (var route in routes)
        {
            var who = VariantPrefix + route;

            if (route == "routine")
            {
                yield return new EncounterDefinition(
                    "route.routine.meet",
                    new EncounterPlace(Id: setting.RoutinePlace, AloneVisitsBefore: 2),
                    Requires: ["!routine.met"],
                    Sets: ["routine.met", "routine.place=" + TurnPlanner.PlaceValue],
                    With: [who],
                    Priority: 60,
                    Text: "Third time at {place}, and the same face is here again. Today they say hello: {who}.");
            }

            if (route == "introduced")
            {
                yield return new EncounterDefinition(
                    "route.introduced.meet",
                    new EncounterPlace(),
                    Time: [TimeOfDay.Evening],
                    Days: [FirstDateDay, setting.Days],
                    Requires: [$"{MainLiRef}.dating", $"{EncounterEvaluator.InviteKey}={MainLiRef}", "!introduced.met"],
                    Sets: ["introduced.met", "introduced.place=" + TurnPlanner.PlaceValue],
                    With: [MainLiRef, who],
                    Priority: OpeningPriority + 6,
                    Text: "Halfway through the evening {main_li} waves someone over: {who}, an old friend, who stays for a drink.");
            }

            yield return new EncounterDefinition(
                $"route.{route}.contact",
                new EncounterPlace(PlaceFlag: $"{route}.place"),
                Requires: [$"{route}.met", $"!{route}.contact", $"!{route}.contact_declined"],
                With: [who],
                Priority: 70,
                Text: "{who} is at {place} again, and this time the two of you talk properly.",
                Choices:
                [
                    new("swap-numbers", "Ask for their number", [$"{route}.contact"], ["adventure"]),
                    new("let-it-go", "Keep it friendly", [$"{route}.contact_declined"], ["independence"]),
                ]);

            yield return new EncounterDefinition(
                FirstDateIdFor(route),
                new EncounterPlace(),
                Days: [FirstDateDay, setting.Days],
                Requires: [$"{route}.contact", $"!{route}.first_date", $"{EncounterEvaluator.InviteKey}={route}"],
                Sets: [$"{route}.first_date"],
                With: [who],
                Priority: OpeningPriority + 5,
                Text: "{who} is already at {place} for the {slot} the two of you agreed on.");
        }
    }

    /// <summary>
    /// Every love interest's arc (plan §7): reveal, obstacle, crisis and resolution, tied to their
    /// primary want. Generated per person where they are usually found, with the want and need
    /// filled in at play time (<c>{want}</c>, <c>{need}</c>, <c>helps:{want}</c>).
    /// </summary>
    /// <remarks>
    /// Every crisis lands on the setting's last event, and each needs the player to bring that person
    /// along. Only one person can be brought, so the player cannot help everyone: plan §7's
    /// conflicting crises, made a choice the player sees.
    /// </remarks>
    private static IEnumerable<EncounterDefinition> ArcBeats(SettingDefinition setting, IReadOnlyList<string> routes)
    {
        IEnumerable<string> keys = setting.Openings.Count > 0 ? [MainLiRef, .. routes] : routes;
        var crisisEvent = setting.Events.OrderBy(e => e.Day).LastOrDefault();
        var want = StoryContent.WantToken;

        foreach (var key in keys)
        {
            var who = key == MainLiRef ? MainLiRef : VariantPrefix + key;
            var usual = new EncounterPlace(PlaceFlag: key == MainLiRef ? "main_li.home_place" : $"{key}.place");

            yield return new EncounterDefinition(
                $"arc.{key}.reveal",
                usual,
                Days: [ArcRevealDay, setting.Days],
                Requires: [$"{key}.contact", $"!{key}.want_revealed"],
                Sets: [$"{key}.want_revealed"],
                With: [who],
                Priority: ArcPriority,
                Text: "Somewhere in a long conversation at {place}, {who} admits what they really want: to {want}.");

            yield return new EncounterDefinition(
                $"arc.{key}.obstacle",
                usual,
                Days: [ArcObstacleDay, setting.Days],
                Requires: [$"{key}.want_revealed", $"{key}.first_date", $"!{key}.obstacle"],
                Sets: [$"{key}.obstacle"],
                With: [who],
                Priority: ArcPriority,
                Text: "{who} is quiet today. Something has got in the way of their plan to {want}.",
                Choices:
                [
                    new("think-it-through", "Help them think it through", [$"{key}.obstacle_helped"], [StoryContent.HelpsPrefix + want, "attentiveness"]),
                    new("be-realistic", "Tell them to be realistic", [$"{key}.obstacle_doubted"], [StoryContent.HindersPrefix + want, "honesty"]),
                ]);

            if (crisisEvent is null)
            {
                continue;
            }

            yield return new EncounterDefinition(
                $"arc.{key}.crisis",
                new EncounterPlace(Id: crisisEvent.Place),
                Time: [crisisEvent.Time],
                Days: [crisisEvent.Day, crisisEvent.Day],
                Requires: [$"{key}.obstacle", $"{EncounterEvaluator.InviteKey}={key}"],
                With: [who],
                Priority: EventPriority + 1,
                Text: $"{crisisEvent.Name} is in full swing when {{who}}'s plan to {{want}} comes apart, right here.",
                Choices:
                [
                    new("help", "Step in and help", [$"{key}.crisis_resolved"], [StoryContent.HelpsPrefix + want, "kindness"]),
                    new("stay-out", "Let them handle it", [$"{key}.crisis_skipped"], ["independence"]),
                    new("make-it-worse", "Say it was never going to work", [$"{key}.crisis_worsened"], [StoryContent.HindersPrefix + want]),
                ]);

            yield return new EncounterDefinition(
                $"arc.{key}.resolution",
                usual,
                Requires: [$"{key}.crisis_resolved"],
                With: [who],
                Priority: ArcPriority,
                Text: "When it is all over, {who} finally says what they need: {need}.",
                Choices:
                [
                    new("hear-them", "Take it seriously", [$"{key}.need_addressed"], ["attentiveness"]),
                    new("reassure", "Tell them it will all work out", [$"{key}.need_missed"], ["stability"]),
                ]);
        }
    }

    private static void Validate(SettingDefinition setting, IReadOnlyList<EncounterDefinition> encounters, IReadOnlyList<string>? routes)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var places = setting.Places.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var encounter in encounters)
        {
            void Fail(string message) =>
                throw new InvalidOperationException($"Encounter '{encounter.Id}' in setting '{setting.Id}': {message}");

            if (string.IsNullOrWhiteSpace(encounter.Id) || !ids.Add(encounter.Id))
            {
                Fail("has a blank or duplicate id.");
            }

            if (encounter.Place.Id is { } place && !places.Contains(place))
            {
                Fail($"happens at '{place}', which is not one of the setting's places.");
            }

            if (encounter.Place.PlaceFlag is { } placeFlag && !FlagKey().IsMatch(placeFlag))
            {
                Fail($"reads its place from '{placeFlag}', which is not a valid flag key.");
            }

            if (encounter.Place.AloneVisitsBefore is < 1)
            {
                Fail("needs at least one earlier solo visit, or no solo-visit condition.");
            }

            if (encounter.Days is { } days &&
                (days.Count != 2 || days[0] < 1 || days[0] > days[1] || days[1] > setting.Days))
            {
                Fail($"has days [{string.Join(", ", days)}]; it needs [first, last] within days 1-{setting.Days}.");
            }

            foreach (var expression in encounter.Requires ?? [])
            {
                if (!FlagExpression().IsMatch(expression))
                {
                    Fail($"requires '{expression}', which is not key, !key or key=value.");
                }
            }

            foreach (var set in encounter.Sets ?? [])
            {
                if (!FlagAssignment().IsMatch(set))
                {
                    Fail($"sets '{set}', which is not key or key=value.");
                }
            }

            foreach (var reveal in encounter.Reveals ?? [])
            {
                if (!places.Contains(reveal))
                {
                    Fail($"reveals '{reveal}', which is not one of the setting's places.");
                }
            }

            foreach (var who in encounter.With ?? [])
            {
                if (!WithRef().IsMatch(who))
                {
                    Fail($"is with '{who}'; use main_li or variant:{{route}}.");
                }
                else if (routes is not null
                         && who.StartsWith(VariantPrefix, StringComparison.Ordinal)
                         && !routes.Contains(who[VariantPrefix.Length..]))
                {
                    Fail($"is with '{who}', but the cast has no route '{who[VariantPrefix.Length..]}'. Known: {string.Join(", ", routes)}.");
                }
            }

            if (string.IsNullOrWhiteSpace(encounter.Text))
            {
                Fail("has no text.");
            }

            var choiceIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var choice in encounter.Choices ?? [])
            {
                if (string.IsNullOrWhiteSpace(choice.Id) || !choiceIds.Add(choice.Id) || string.IsNullOrWhiteSpace(choice.Text))
                {
                    Fail($"has a choice with a blank or duplicate id, or no text.");
                }

                if ((choice.Tags ?? []).Any(string.IsNullOrWhiteSpace))
                {
                    Fail($"choice '{choice.Id}' has a blank tag.");
                }

                foreach (var set in choice.Sets ?? [])
                {
                    if (!FlagAssignment().IsMatch(set))
                    {
                        Fail($"choice '{choice.Id}' sets '{set}', which is not key or key=value.");
                    }
                }
            }

            if (encounter.Choices is { Count: 1 })
            {
                Fail("offers a choice with only one answer.");
            }

            var texts = (encounter.Choices ?? []).Select(c => c.Text).Append(encounter.Text);
            foreach (Match token in texts.SelectMany(t => Token().Matches(t)))
            {
                if (!TextTokens.Contains(token.Groups[1].Value, StringComparer.Ordinal))
                {
                    Fail($"uses '{token.Value}' in its text. Known: {string.Join(", ", TextTokens.Select(t => "{" + t + "}"))}.");
                }
            }
        }
    }

    [GeneratedRegex(@"\{([^{}]*)\}")]
    private static partial Regex Token();

    private static IReadOnlyList<EncounterDefinition> Read(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<List<EncounterDefinition>>(File.ReadAllText(path), Json)
              ?? throw new InvalidOperationException($"Encounter file '{path}' deserialised to null.")
            : [];

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._:-]*$")]
    private static partial Regex FlagKey();

    [GeneratedRegex(@"^!?[a-z0-9][a-z0-9._:-]*(=[^=\s]+)?$")]
    private static partial Regex FlagExpression();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._:-]*(=[^=\s]+)?$")]
    private static partial Regex FlagAssignment();

    [GeneratedRegex(@"^(main_li|variant:[a-z0-9-]+)$")]
    private static partial Regex WithRef();
}
