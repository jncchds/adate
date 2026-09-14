using Game.Core.Cast;
using Game.Core.Encounters;
using Game.Core.Places;
using Game.Core.Saves;
using Game.Core.Settings;
using Game.Core.Story;
using Game.Core.World;
using Game.Data.Repositories;
using Microsoft.Extensions.Options;

namespace Game.Host.Services;

/// <summary>An encounter's choice that is still open, with its text filled in.</summary>
public sealed record PendingChoice(string EncounterId, string Text, IReadOnlyList<EncounterChoice> Choices);

/// <param name="Today">Setting events held today, whose places are known for the day.</param>
/// <param name="Opening">The opening the player chose, or null if they have not chosen yet.</param>
/// <param name="Hints">Where the story expects the player to look next, while the opening's beats are open.</param>
/// <param name="Pending">A choice the player must answer before the next turn.</param>
/// <param name="CanInvite">Whether the player may bring the main LI along this turn.</param>
public sealed record PlayState(
    SettingDefinition Setting,
    ClockState Clock,
    IReadOnlyList<PlaceRecord> KnownPlaces,
    IReadOnlyList<SettingEvent> Today,
    bool Over,
    Guid? MainLiId,
    string MainLiName,
    SettingOpening? Opening,
    IReadOnlyList<string> Hints,
    PendingChoice? Pending,
    bool CanInvite,
    IReadOnlyList<RelationshipView>? Relationships = null);

/// <summary>Where the player stands with someone they have met.</summary>
public sealed record RelationshipView(string Name, RelationshipState State);

/// <summary>A save's setting, places, clock, openings, choices and turns.</summary>
public sealed class WorldService(
    SaveRepository saves,
    PlaceRepository places,
    CharacterRepository characters,
    GameStateRepository state,
    StoryStateRepository story,
    ISettingCatalog settings,
    IEncounterCatalog encounters,
    StoryContent storyContent,
    IOptions<StudioOptions> options)
{
    private readonly RelationshipEngine _engine = new(storyContent);

    /// <summary>
    /// The places the player can choose between, after making sure the save has its setting's
    /// authored places. A save created before settings existed is given the default setting, once.
    /// </summary>
    public async Task<IReadOnlyList<PlaceRecord>> KnownPlacesAsync(SaveId saveId, CancellationToken ct = default)
    {
        await EnsureSettingAsync(saveId, ct).ConfigureAwait(false);
        return await ListKnownAsync(saveId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Where the save stands. Starts the clock on a save that has none. An event's place becomes
    /// known on the event day.
    /// </summary>
    public async Task<PlayState> GetPlayStateAsync(SaveId saveId, CancellationToken ct = default)
    {
        var setting = await EnsureSettingAsync(saveId, ct).ConfigureAwait(false);
        var clock = await state.GetOrStartClockAsync(saveId, ct).ConfigureAwait(false);

        foreach (var placeId in TurnPlanner.DayStartReveals(setting, clock.Day))
        {
            await places.MarkKnownAsync(saveId, placeId, clock.Day, ct).ConfigureAwait(false);
        }

        var known = await ListKnownAsync(saveId, ct).ConfigureAwait(false);
        var flags = await state.GetFlagsAsync(saveId, ct).ConfigureAwait(false);
        var names = await NamesAsync(saveId, ct).ConfigureAwait(false);

        var opening = flags.TryGetValue("opening", out var openingId)
            ? setting.Openings.FirstOrDefault(o => o.Id == openingId)
            : null;

        PendingChoice? pending = null;
        if (EncounterEvaluator.Holds(flags, EncounterEvaluator.PendingChoiceKey))
        {
            var encounter = encounters.For(setting.Id).First(e => e.Id == flags[EncounterEvaluator.PendingChoiceKey]);
            pending = new PendingChoice(
                encounter.Id,
                Fill(encounter.Text, names, PlaceName(setting, known, flags.GetValueOrDefault("main_li.home_place")), clock),
                [.. (encounter.Choices ?? []).Select(c => c with { Text = Fill(c.Text, names, "", clock) })]);
        }

        var over = clock.IsPast(setting.Days);

        return new PlayState(
            setting,
            clock,
            known,
            [.. setting.Events.Where(e => e.Day == clock.Day)],
            over,
            names.MainLiId,
            names.MainLi,
            opening,
            Hints(setting, opening, flags, clock, names.MainLi),
            pending,
            !over && pending is null && CanInvite(setting, known, flags, clock),
            await RelationshipsAsync(saveId, names, flags, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Records the player's opening: the clock starts at the opening's time, and its meeting, home
    /// and second places become known.
    /// </summary>
    public async Task ChooseOpeningAsync(SaveId saveId, string openingId, CancellationToken ct = default)
    {
        var setting = await EnsureSettingAsync(saveId, ct).ConfigureAwait(false);

        var opening = setting.Openings.FirstOrDefault(o => o.Id == openingId)
            ?? throw new InvalidOperationException($"Setting '{setting.Id}' has no opening '{openingId}'.");

        string?[] reveal = [opening.MeetingPlace, opening.HomePlace, opening.SecondPlace];

        await state.StartOpeningAsync(
            saveId,
            opening.Id,
            opening.HomePlace,
            new ClockState(1, opening.Time),
            [.. reveal.OfType<string>().Distinct(StringComparer.Ordinal)],
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Spends the current slot at <paramref name="placeId"/>, which must be a place the player knows,
    /// optionally bringing the main LI along.
    /// </summary>
    public async Task<TurnOutcome> TakeTurnAsync(SaveId saveId, string placeId, bool invite = false, CancellationToken ct = default)
    {
        var play = await GetPlayStateAsync(saveId, ct).ConfigureAwait(false);

        if (play.Over)
        {
            throw new InvalidOperationException($"The {play.Setting.Days} days of this save are over.");
        }

        if (play.Pending is not null)
        {
            throw new InvalidOperationException("Answer the open choice before taking another turn.");
        }

        if (invite && !play.CanInvite)
        {
            throw new InvalidOperationException($"{play.MainLiName} cannot be invited along right now.");
        }

        var place = play.KnownPlaces.FirstOrDefault(p => p.Id == placeId)
            ?? throw new InvalidOperationException($"'{placeId}' is not a place the player knows.");

        var flags = new Dictionary<string, string>(await state.GetFlagsAsync(saveId, ct).ConfigureAwait(false), StringComparer.Ordinal);
        if (invite)
        {
            // Transient: it shapes this turn's pick and is never stored.
            flags[EncounterEvaluator.InviteKey] = "main_li";
        }

        var context = new TurnContext(
            play.Clock,
            place.Id,
            flags,
            await state.CountAloneVisitsAsync(saveId, place.Id, ct).ConfigureAwait(false));

        var outcome = TurnPlanner.Plan(play.Setting, encounters.For(play.Setting.Id), context, place.Name);

        if (invite && !outcome.With.Contains("main_li"))
        {
            throw new InvalidOperationException($"Nothing here would bring {play.MainLiName} along.");
        }

        // The main LI's relationship moves with the turn and commits with it: a date at a place
        // they like or dislike, and any stage the turn's flags now allow.
        var relationships = new Dictionary<Guid, RelationshipState>();
        if (outcome.With.Contains("main_li") && await MainStoryAsync(saveId, play.Setting, ct).ConfigureAwait(false) is { } main)
        {
            var current = await story.GetRelationshipAsync(saveId, main.Id, ct).ConfigureAwait(false);
            if (outcome.EncounterId == JsonEncounterCatalog.FirstDateId)
            {
                current = _engine.DateAt(current, main.Profile, place.TypeId, play.Clock.Day);
            }

            var after = new Dictionary<string, string>(flags, StringComparer.Ordinal);
            after.Remove(EncounterEvaluator.InviteKey);
            foreach (var (key, value) in outcome.FlagsToSet)
            {
                after[key] = value;
            }

            relationships[main.Id] = _engine.Advance(current, StageFacts.FromFlags(after, "main_li"), main.Member.Temper);
        }

        await state.CommitTurnAsync(saveId, outcome, relationships, ct).ConfigureAwait(false);

        var names = await NamesAsync(saveId, ct).ConfigureAwait(false);
        return outcome with
        {
            Text = Fill(outcome.Text, names, place.Name, outcome.VisitedAt),
            Choices = [.. (outcome.Choices ?? []).Select(c => c with { Text = Fill(c.Text, names, place.Name, outcome.VisitedAt) })],
        };
    }

    /// <summary>Answers the open choice.</summary>
    public async Task ChooseAsync(SaveId saveId, string choiceId, CancellationToken ct = default)
    {
        var play = await GetPlayStateAsync(saveId, ct).ConfigureAwait(false);

        var pending = play.Pending
            ?? throw new InvalidOperationException("There is no open choice.");

        var choice = pending.Choices.FirstOrDefault(c => c.Id == choiceId)
            ?? throw new InvalidOperationException($"'{choiceId}' is not an answer to the open choice.");

        var sets = TurnPlanner.Assignments(choice.Sets ?? []);

        // Every choice so far is in a scene with the main LI, so its tags are scored for them.
        // Variant routes (build step 7) score whoever is present.
        var relationships = new Dictionary<Guid, RelationshipState>();
        if (await MainStoryAsync(saveId, play.Setting, ct).ConfigureAwait(false) is { } main)
        {
            var current = await story.GetRelationshipAsync(saveId, main.Id, ct).ConfigureAwait(false);
            var delta = _engine.Score(main.Profile, main.Member.Temper, main.Member.WantId, choice.Tags ?? []);
            current = _engine.Apply(current, delta, play.Clock.Day);

            var after = new Dictionary<string, string>(await state.GetFlagsAsync(saveId, ct).ConfigureAwait(false), StringComparer.Ordinal);
            foreach (var (key, value) in sets)
            {
                after[key] = value;
            }

            relationships[main.Id] = _engine.Advance(current, StageFacts.FromFlags(after, "main_li"), main.Member.Temper);
        }

        await state.ResolveChoiceAsync(saveId, pending.EncounterId, choice.Id, sets, relationships, ct).ConfigureAwait(false);
    }

    private sealed record MainStory(Guid Id, CastMember Member, StoryProfile Profile);

    /// <summary>
    /// The main LI with their cast record and story profile, the profile built and stored the first
    /// time it is needed. Null for a save without a stored cast, which has nothing to score against.
    /// </summary>
    private async Task<MainStory?> MainStoryAsync(SaveId saveId, SettingDefinition setting, CancellationToken ct)
    {
        var main = await characters.GetMainAsync(saveId, ct).ConfigureAwait(false);
        var cast = main is null ? null : await characters.GetCastAsync(main.Id, ct).ConfigureAwait(false);
        if (main is null || cast is null)
        {
            return null;
        }

        var profile = await story.GetProfileAsync(main.Id, ct).ConfigureAwait(false);
        if (profile is null)
        {
            var placeTypes = setting.Places.Select(p => p.Type).Distinct(StringComparer.Ordinal).ToList();
            var generated = StoryProfileGenerator.For(cast, storyContent, placeTypes, main.AnchorSeed ?? 0);
            profile = await story.SetProfileAsync(main.Id, generated[0], ct).ConfigureAwait(false);
        }

        return new MainStory(main.Id, cast[0], profile);
    }

    private async Task<IReadOnlyList<RelationshipView>> RelationshipsAsync(
        SaveId saveId,
        Names names,
        IReadOnlyDictionary<string, string> flags,
        CancellationToken ct)
    {
        if (names.MainLiId is not { } mainId || !EncounterEvaluator.Holds(flags, "main_li.met"))
        {
            return [];
        }

        return [new RelationshipView(names.MainLi, await story.GetRelationshipAsync(saveId, mainId, ct).ConfigureAwait(false))];
    }

    private sealed record Names(Guid? MainLiId, string MainLi, string Player);

    private async Task<Names> NamesAsync(SaveId saveId, CancellationToken ct)
    {
        var main = await characters.GetMainAsync(saveId, ct).ConfigureAwait(false);
        var mainName = main is null ? null : await characters.GetNameAsync(main.Id, ct).ConfigureAwait(false);
        var player = await saves.GetPlayerNameAsync(saveId, ct).ConfigureAwait(false);

        return new Names(main?.Id, string.IsNullOrWhiteSpace(mainName) ? "them" : mainName, string.IsNullOrWhiteSpace(player) ? "you" : player);
    }

    private static string Fill(string text, Names names, string place, ClockState clock) =>
        text.Replace("{main_li}", names.MainLi, StringComparison.Ordinal)
            .Replace("{player}", names.Player, StringComparison.Ordinal)
            .Replace("{place}", place, StringComparison.Ordinal)
            .Replace("{slot}", clock.Slot.ToString().ToLowerInvariant(), StringComparison.Ordinal);

    private static string PlaceName(SettingDefinition setting, IReadOnlyList<PlaceRecord> known, string? placeId) =>
        placeId is null ? "" : known.FirstOrDefault(p => p.Id == placeId)?.Name ?? setting.Places.FirstOrDefault(p => p.Id == placeId)?.Name ?? placeId;

    /// <summary>The beats' windows, said out loud so a player is never left guessing where the story went.</summary>
    private static IReadOnlyList<string> Hints(
        SettingDefinition setting,
        SettingOpening? opening,
        IReadOnlyDictionary<string, string> flags,
        ClockState clock,
        string mainLi)
    {
        if (opening is null)
        {
            return [];
        }

        string Name(string id) => setting.Places.First(p => p.Id == id).Name;
        bool Has(string key) => EncounterEvaluator.Holds(flags, key);

        if (!Has("main_li.met") && clock.Day <= 2)
        {
            return [$"{opening.Name}: it starts at {Name(opening.MeetingPlace)}."];
        }

        if (Has("main_li.met") && !Has("main_li.recognised"))
        {
            if (clock.Day <= 4)
            {
                return [$"{mainLi} said they are usually at {Name(opening.HomePlace)}, {opening.HomeWindow}."];
            }

            if (clock.Day <= 7 && opening.SecondPlace is { } second)
            {
                return [$"You missed {mainLi} at {Name(opening.HomePlace)}. They also mentioned {Name(second)}."];
            }
        }

        if (Has("main_li.contact") && !Has("main_li.first_date") && clock.Day < JsonEncounterCatalog.FirstDateDay)
        {
            return [$"You have {mainLi}'s number. Ask them somewhere from day {JsonEncounterCatalog.FirstDateDay}."];
        }

        return [];
    }

    private bool CanInvite(SettingDefinition setting, IReadOnlyList<PlaceRecord> known, IReadOnlyDictionary<string, string> flags, ClockState clock)
    {
        if (clock.IsPast(setting.Days))
        {
            return false;
        }

        var withInvite = new Dictionary<string, string>(flags, StringComparer.Ordinal) { [EncounterEvaluator.InviteKey] = "main_li" };
        var inviteBeats = encounters.For(setting.Id)
            .Where(e => (e.Requires ?? []).Contains($"{EncounterEvaluator.InviteKey}=main_li"))
            .ToList();

        return known.Any(place =>
            inviteBeats.Any(e => EncounterEvaluator.Matches(e, new TurnContext(clock, place.Id, withInvite, 0))));
    }

    private async Task<SettingDefinition> EnsureSettingAsync(SaveId saveId, CancellationToken ct)
    {
        var settingId = await saves.GetSettingIdAsync(saveId, ct).ConfigureAwait(false)
            ?? await saves.SetSettingAsync(saveId, options.Value.DefaultSettingId, ct).ConfigureAwait(false);

        var setting = settings.Get(settingId);
        await places.AddAsync(PlaceRecord.Authored(saveId, setting), ct).ConfigureAwait(false);
        return setting;
    }

    private async Task<IReadOnlyList<PlaceRecord>> ListKnownAsync(SaveId saveId, CancellationToken ct)
    {
        var known = await places.ListAsync(saveId, knownOnly: true, ct).ConfigureAwait(false);

        return known.Count > 0
            ? known
            : throw new InvalidOperationException($"Save '{saveId}' has no known places.");
    }
}
