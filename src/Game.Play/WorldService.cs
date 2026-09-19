using System.Text.Json;
using Game.Core;
using Game.Core.Cast;
using Game.Core.Encounters;
using Game.Core.Places;
using Game.Core.Saves;
using Game.Core.Scenes;
using Game.Core.Settings;
using Game.Core.Story;
using Game.Core.World;
using Game.Data.Repositories;
using Game.Llm;
using Microsoft.Extensions.Options;

namespace Game.Play;

/// <summary>An encounter's choice that is still open, with its text filled in.</summary>
public sealed record PendingChoice(string EncounterId, string Text, IReadOnlyList<EncounterChoice> Choices);

/// <summary>Someone the player can bring along this turn, or end the story with.</summary>
/// <param name="Key">The invite value and flag prefix: <c>main_li</c> or a route id.</param>
public sealed record Invitee(string Key, string Name);

/// <summary>What a turn's scene shows: its text, who it is about, and who stands on the stage wearing which expression.</summary>
/// <param name="CharacterId">The person the scene is about, or null when it is about no one in the cast.</param>
/// <param name="Aesthetic">Their style, which picks the sprite's outfit.</param>
/// <param name="Choices">Replies the player may give, when the scene waits for one.</param>
public sealed record SceneView(
    string Text,
    Guid? CharacterId,
    string? Name,
    string? Aesthetic,
    string? Expression,
    IReadOnlyList<ProposedChoice>? Choices = null)
{
    /// <summary>Whether the scene waits for the player's reply: their own words, or one of <see cref="Choices"/>.</summary>
    public bool Open { get; init; }

    /// <summary>
    /// Who stands on the stage, left to right: only those the words have brought in and not seen leave, so it can be
    /// nobody, the person the scene is about, or two people. Empty for texting.
    /// </summary>
    public IReadOnlyList<SceneFigure> Figures { get; init; } = [];

    /// <summary>
    /// Whether <see cref="Text"/> is the authored placeholder, because the writer failed while a model
    /// was configured. The player is shown a warning, and can ask for the words again.
    /// </summary>
    public bool Fallback { get; init; }
}

/// <summary>The other people's reaction to a reply, and a popup when the reaction was considerable.</summary>
/// <param name="Agreed">A meeting the reply settled, now held as a promise.</param>
/// <param name="Transcript">The scene so far including this exchange, as the waiting scene now holds it.</param>
/// <param name="Next">What the player can say next when the conversation goes on; empty when nothing was proposed.</param>
/// <param name="Open">Whether the scene still takes a reply: the player's own words even when nothing was proposed.</param>
/// <param name="Redressed">Whether the person put something on or changed, so their sprite is drawn again even at the same expression.</param>
/// <param name="Together">Whether the two just set off somewhere together, so there is nobody to text and nowhere else to go.</param>
/// <param name="Fallback">Whether the answer is the placeholder because the writer failed: the player is warned, and can ask again.</param>
public sealed record ReactionResult(
    SceneView View,
    string? Popup,
    string? Agreed = null,
    string Transcript = "",
    IReadOnlyList<ProposedChoice>? Next = null,
    bool Open = false,
    bool Redressed = false,
    bool Together = false,
    bool Fallback = false);

/// <summary>Where the player stands with someone they have met.</summary>
/// <param name="Left">Why they walked away, or null while they are still around.</param>
public sealed record RelationshipView(string Name, RelationshipState State, string? Left = null);

/// <summary>The ending check: who can be chosen (alone always can), and who has already left.</summary>
/// <param name="Asker">Who asks the player for an answer: the one on offer who cares most, or nobody.</param>
public sealed record EndingOffer(IReadOnlyList<Invitee> Routes, IReadOnlyList<string> Departures, string? Asker = null);

/// <summary>What the playthrough revealed (plan §9), stored once the story ends.</summary>
/// <param name="ProfileOf">Whose profile is shown: the person the player ended with, or the one they were closest to.</param>
public sealed record EndingRecap(
    EndingKind Kind,
    string Text,
    string? PartnerName,
    IReadOnlyList<string> PassedOver,
    IReadOnlyList<string> Departures,
    string? ProfileOf,
    IReadOnlyList<string> Profile,
    IReadOnlyList<RecapLine>? Choices = null);

/// <summary>
/// The scene the player is in, as saved while it happened, so leaving and coming back shows exactly
/// what was on screen: the same place, picture, person, words and reply.
/// </summary>
/// <param name="Written">Whether <paramref name="Text"/> is the finished scene; false when the writing was interrupted.</param>
/// <param name="BackgroundPath">The place's picture, once drawn.</param>
/// <param name="SpritePath">The person as last shown, once drawn.</param>
/// <param name="Exchanges">The conversation so far: each reply the player gave and the answer to it.</param>
public sealed record CurrentScene(
    TurnOutcome Outcome,
    bool Written,
    string Text,
    string? BackgroundPath,
    Guid? CharacterId,
    string? Speaker,
    string? Expression,
    string? SpritePath,
    IReadOnlyList<SceneExchange> Exchanges,
    bool Fallback = false)
{
    /// <summary>Who stands on the stage, left to right, as the words last left it.</summary>
    public IReadOnlyList<SceneFigure> Figures { get; init; } = [];
}

/// <param name="Today">Setting events held today, whose places are known for the day.</param>
/// <param name="Opening">The opening the player chose, or null if they have not chosen yet.</param>
/// <param name="Hints">Where the story expects the player to look next, while the opening's beats are open.</param>
/// <param name="Pending">A choice the player must answer before the next turn.</param>
/// <param name="Invitees">Who the player may bring along this turn.</param>
/// <param name="People">Display names by encounter reference: <c>main_li</c> or <c>variant:{route}</c>.</param>
/// <param name="EndingOffer">Set when the ending check is due and the player has not picked yet.</param>
/// <param name="Ending">Set once the story has ended.</param>
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
    IReadOnlyList<Invitee> Invitees,
    IReadOnlyList<RelationshipView> Relationships,
    IReadOnlyDictionary<string, string> People,
    EndingOffer? EndingOffer,
    EndingRecap? Ending,
    WeatherDefinition? Weather = null,
    PendingScene? PendingScene = null,
    CurrentScene? Scene = null,
    string? LastPlaceId = null,
    IReadOnlyList<Invitee>? Met = null,
    int CastSize = 0,
    PlayerJob? Job = null,
    bool OnShift = false,
    IReadOnlyList<Invitee>? Contacts = null,
    HeadingTogether? Heading = null);

/// <summary>
/// Where the player is on their way to with someone, straight away (user request: going together must lock out other
/// options, or standing them up is one). The map offers only that place, and nobody else can be texted or invited.
/// </summary>
public sealed record HeadingTogether(string PlaceId, string PlaceName, string Name);

/// <summary>A save's setting, places, clock, openings, choices, turns and ending.</summary>
/// <summary>One part of the debug screen: a title and its raw lines.</summary>
public sealed record DebugSection(string Title, IReadOnlyList<string> Lines);

public sealed class WorldService(
    SaveRepository saves,
    PlaceRepository places,
    CharacterRepository characters,
    GameStateRepository state,
    StoryStateRepository story,
    SceneLogRepository sceneLog,
    ThreadRepository threads,
    PlanRepository plans,
    Game.Core.Content.ILocationCatalog placeTypes,
    ISettingCatalog settings,
    IEncounterCatalog encounters,
    StoryContent storyContent,
    CastContent castContent,
    RouteContent routes,
    EndingContent endingContent,
    WeatherContent weatherContent,
    HappeningContent happenings,
    SceneWriter sceneWriter,
    ReactionWriter reactionWriter,
    EpilogueWriter epilogueWriter,
    BibleWriter bibleWriter,
    VoiceWriter voiceWriter,
    ThreadWriter threadWriter,
    PlanWriter planWriter,
    NameWriter nameWriter,
    MemoryRepository memoryStore,
    MemoryCompactor compactor,
    IEmbeddingClient embeddings,
    CharacterStudio studio,
    IOptions<LlmOptions> llmOptions,
    IOptions<StudioOptions> options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The beats each save's planning rewrote, by encounter id, read once when its setting is first put
    /// together. A plan never changes after it is laid out, so this only ever grows by one save.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> _encounterTexts =
        new(StringComparer.Ordinal);

    private readonly RelationshipEngine _engine = new(storyContent);
    private readonly EndingRules _endings = new(endingContent, storyContent);

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
        var cast = await CastAsync(saveId, setting, ct).ConfigureAwait(false);
        var names = await NamesAsync(saveId, cast, ct).ConfigureAwait(false);

        var opening = flags.TryGetValue("opening", out var openingId)
            ? setting.Openings.FirstOrDefault(o => o.Id == openingId)
            : null;

        EndingRecap? ending = null;
        EndingOffer? offer = null;
        if (await story.GetEndingAsync(saveId, ct).ConfigureAwait(false) is { } stored)
        {
            ending = JsonSerializer.Deserialize<EndingRecap>(stored.SummaryJson, Json);
        }
        else if (opening is not null && cast.Count > 0)
        {
            var statuses = await StatusesAsync(saveId, cast, flags, null, ct).ConfigureAwait(false);
            if (EndingRules.IsDue(clock, setting.Days, statuses))
            {
                var onOffer = EndingRules.Offer(statuses);
                var asker = statuses
                    .Where(s => onOffer.Contains(s.Key))
                    .OrderByDescending(s => s.State.Affection)
                    .Select(s => cast.First(li => li.Key == s.Key).Name)
                    .FirstOrDefault();

                offer = new EndingOffer(
                    [.. onOffer.Select(key => new Invitee(key, cast.First(li => li.Key == key).Name))],
                    Departures(cast, flags),
                    asker);
            }
        }

        PendingChoice? pending = null;
        if (EncounterEvaluator.Holds(flags, EncounterEvaluator.PendingChoiceKey))
        {
            var encounter = Encounter(saveId, setting, flags[EncounterEvaluator.PendingChoiceKey]);
            var placeId = encounter.Place.Id ?? (encounter.Place.PlaceFlag is { } placeFlag ? flags.GetValueOrDefault(placeFlag) : null);
            var owner = Owner(cast, encounter.With ?? []);

            // An encounter that no longer offers choices (the contact beats became conversations) leaves nothing
            // to answer, even in a save that stopped at it.
            pending = (encounter.Choices ?? []).Count == 0 ? null : new PendingChoice(
                encounter.Id,
                Fill(encounter.Text, names, owner, PlaceName(setting, known, placeId), clock),
                [.. (encounter.Choices ?? []).Select(c => c with { Text = Fill(c.Text, names, owner, "", clock) })]);
        }

        var over = clock.IsPast(setting.Days);
        var pendingScene = await state.GetPendingSceneAsync(saveId, ct).ConfigureAwait(false);
        var openPromises = await story.GetPromisesAsync(saveId, openOnly: true, ct).ConfigureAwait(false);
        var heading = await HeadingAsync(saveId, setting, cast, flags, known, clock, openPromises, ct).ConfigureAwait(false);

        IReadOnlyList<Invitee> invitees = over || pending is not null || pendingScene is not null || offer is not null || ending is not null || heading is not null
            ? []
            : [.. cast.Where(li => !HasLeft(flags, li) && CanInvite(saveId, setting, cast, known, flags, clock, li.Key)).Select(li => new Invitee(li.Key, li.Name))];

        var relationships = new List<RelationshipView>();
        foreach (var li in cast.Where(li => EncounterEvaluator.Holds(flags, $"{li.Key}.met")))
        {
            relationships.Add(new RelationshipView(
                li.Name,
                await story.GetRelationshipAsync(saveId, li.Id, ct).ConfigureAwait(false),
                HasLeft(flags, li) ? DepartureText(li, flags[$"{li.Key}.left"]) : null));
        }

        var people = cast.ToDictionary(li => li.Ref, li => li.Name, StringComparer.Ordinal);
        people.TryAdd(JsonEncounterCatalog.MainLiRef, names.MainLi);

        // The player's own life: a shift that is now, people they can text, and the traits they are building.
        var onShift = PlayerLife.OnShift(setting.Job, clock);
        IReadOnlyList<Invitee> contacts = heading is not null
            ? []
            : [.. cast.Where(li => EncounterEvaluator.Holds(flags, $"{li.Key}.contact") && !HasLeft(flags, li)).Select(li => new Invitee(li.Key, li.Name))];
        var building = PlayerLife.Traits(flags)
            .Where(t => PlayerLife.Level(t.Value, storyContent.Rules.TraitLevels) > 0)
            .Select(t => t.Key)
            .Order(StringComparer.Ordinal)
            .ToList();
        var life = new List<string>();
        if (onShift && setting.Job is { } shift)
        {
            life.Add($"Your shift at {PlaceName(setting, known, shift.Place)} is now. Skipping it costs you.");
        }

        if (building.Count > 0)
        {
            life.Add($"What you do is shaping you: {string.Join(", ", building)}.");
        }

        // Meetings the player agreed to, so an arrangement made in conversation is never forgotten.
        IReadOnlyList<string> promised =
        [
            .. openPromises
                .Where(p => p.Kind is PromiseKind.Meet)
                .Select(p => (Promise: p, Person: cast.FirstOrDefault(li => li.Id.ToString() == p.CharacterId), Place: known.FirstOrDefault(k => k.Id == p.PlaceId)))
                .Where(m => m.Person is not null && m.Place is not null)
                .Select(m => MeetingAgreement.IsNow(m.Promise) && Promises.IsDue(m.Promise, clock)
                    ? $"{m.Person!.Name} is going to {m.Place!.Name} with you now."
                    : Promises.IsDue(m.Promise, clock)
                    ? $"You agreed to meet {m.Person!.Name} at {m.Place!.Name} now."
                    : $"You agreed to meet {m.Person!.Name} at {m.Place!.Name} on day {m.Promise.DueDay}" +
                      (m.Promise.DueSlot is { } due ? $", {due.ToString().ToLowerInvariant()}." : ".")),
        ];

        // Everyone met who is still around, in cast order: the map's lineup.
        IReadOnlyList<Invitee> met = [.. cast.Where(li => EncounterEvaluator.Holds(flags, $"{li.Key}.met") && !HasLeft(flags, li)).Select(li => new Invitee(li.Key, li.Name))];

        var open = await sceneLog.GetOpenAsync(saveId, ct).ConfigureAwait(false);
        var scene = open is null
            ? null
            : new CurrentScene(
                TurnOutcomeJson.Deserialize(open.OutcomeJson),
                open.Written,
                open.Text,
                open.BackgroundPath,
                open.CharacterId,
                open.Speaker,
                open.Expression,
                open.SpritePath,
                open.Exchanges,
                open.Fallback) { Figures = open.Figures ?? [] };
        var lastPlaceId = open?.PlaceId ?? await sceneLog.GetLastPlaceIdAsync(saveId, ct).ConfigureAwait(false);

        return new PlayState(
            setting,
            clock,
            known,
            [.. setting.Events.Where(e => e.Day == clock.Day)],
            over,
            names.MainLiId,
            names.MainLi,
            opening,
            [.. life, .. promised, .. Hints(setting, opening, flags, clock, names.MainLi), .. Sightings(saveId, setting, cast, flags, known, clock), .. Routines(saveId, setting, cast, flags, known)],
            pending,
            invitees,
            relationships,
            people,
            offer,
            ending,
            WeatherOn(saveId, setting, clock.Day),
            pendingScene,
            scene,
            lastPlaceId,
            met,
            cast.Count,
            setting.Job,
            onShift,
            contacts,
            heading);
    }

    /// <summary>Where the player is going with someone straight away, when an agreement to go together is due now and they are still around.</summary>
    private async Task<HeadingTogether?> HeadingAsync(
        SaveId saveId, SettingDefinition setting, IReadOnlyList<LoveInterest> cast, IReadOnlyDictionary<string, string> flags,
        IReadOnlyList<PlaceRecord> known, ClockState clock, IReadOnlyList<Promise> openPromises, CancellationToken ct)
    {
        if (MeetingAgreement.Heading(openPromises, clock) is not { } promise
            || cast.FirstOrDefault(li => li.Id.ToString() == promise.CharacterId && !HasLeft(flags, li)) is not { } with)
        {
            return null;
        }

        var place = known.FirstOrDefault(p => p.Id == promise.PlaceId) ?? await places.GetAsync(saveId, promise.PlaceId!, ct).ConfigureAwait(false);
        return new HeadingTogether(promise.PlaceId!, place?.Name ?? PlaceName(setting, known, promise.PlaceId!), with.Name);
    }

    /// <summary>The weather on a day of a save: deterministic, so the same day always looks the same.</summary>
    public WeatherDefinition WeatherOn(SaveId saveId, SettingDefinition setting, int day) =>
        weatherContent.Get(WeatherRoll.For(saveId.ToString(), Math.Clamp(day, 1, setting.Days), weatherContent, setting));

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
    /// optionally bringing someone along by their <see cref="Invitee.Key"/>. When the turn ends a day,
    /// anyone the leaving rules now apply to walks away, in the same transaction.
    /// </summary>
    public async Task<TurnOutcome> TakeTurnAsync(
        SaveId saveId, string placeId, string? invite = null, CancellationToken ct = default)
    {
        var play = await GetPlayStateAsync(saveId, ct).ConfigureAwait(false);

        if (play.Ending is not null || play.EndingOffer is not null)
        {
            throw new InvalidOperationException("The story is at its ending; there are no more turns.");
        }

        if (play.Over)
        {
            throw new InvalidOperationException($"The {play.Setting.Days} days of this save are over.");
        }

        if (play.Pending is not null || play.PendingScene is not null)
        {
            throw new InvalidOperationException("Answer the open choice before taking another turn.");
        }

        if (invite is not null && play.Invitees.All(i => i.Key != invite))
        {
            throw new InvalidOperationException($"'{invite}' cannot be invited along right now.");
        }

        var place = play.KnownPlaces.FirstOrDefault(p => p.Id == placeId)
            ?? throw new InvalidOperationException($"'{placeId}' is not a place the player knows.");

        if (play.Heading is { } heading && heading.PlaceId != place.Id)
        {
            throw new InvalidOperationException($"{heading.Name} is going to {heading.PlaceName} with you: go there.");
        }

        var flags = new Dictionary<string, string>(await state.GetFlagsAsync(saveId, ct).ConfigureAwait(false), StringComparer.Ordinal);
        if (invite is not null)
        {
            // Transient: it shapes this turn's pick and is never stored.
            flags[EncounterEvaluator.InviteKey] = invite;
        }

        var cast = await CastAsync(saveId, play.Setting, ct).ConfigureAwait(false);

        // A meeting the two agreed on, turned up to once the player has their number, is the first date, and it
        // is the only way one happens (user feedback: bringing someone along the next day was called a date).
        var openPromises = await story.GetPromisesAsync(saveId, openOnly: true, ct).ConfigureAwait(false);
        if (openPromises.FirstOrDefault(p => Promises.PutsThere(p, play.Clock, place.Id)) is { } agreedMeeting
            && cast.FirstOrDefault(li => li.Id.ToString() == agreedMeeting.CharacterId && !HasLeft(flags, li)) is { } dateWith
            && EncounterEvaluator.Holds(flags, $"{dateWith.Key}.contact")
            && !EncounterEvaluator.Holds(flags, $"{dateWith.Key}.first_date"))
        {
            // Transient, like an invitation: it shapes this turn's pick and is never stored.
            flags[JsonEncounterCatalog.DateAgreedKey] = dateWith.Key;
        }

        var context = new TurnContext(
            play.Clock,
            place.Id,
            flags,
            await state.CountAloneVisitsAsync(saveId, place.Id, ct).ConfigureAwait(false));

        var outcome = TurnPlanner.Plan(play.Setting, Available(saveId, play.Setting, cast, flags), context, place.Name);

        // Someone not met yet can be run into by chance, instead of what was planned here: never over a story beat
        // that matters more than a chance meeting, a meeting the player agreed to, or bringing someone along.
        var plannedPriority = outcome.EncounterId is { } plannedId
            ? Available(saveId, play.Setting, cast, flags).FirstOrDefault(e => e.Id == plannedId)?.Priority ?? int.MaxValue
            : 0;
        var saveKey = saveId.ToString();
        IReadOnlyList<string> settingPlaces = [.. play.Setting.Places.Select(p => p.Id)];
        if (invite is null
            && plannedPriority <= ChanceMeetingPriority
            && flags.GetValueOrDefault(ChanceMeetingDayKey) != play.Clock.Day.ToString(System.Globalization.CultureInfo.InvariantCulture)
            && !openPromises.Any(p => Promises.PutsThere(p, play.Clock, place.Id))
            && cast
                .Where(li => li.Key != JsonEncounterCatalog.MainLiRef && !EncounterEvaluator.Holds(flags, $"{li.Key}.met") && !HasLeft(flags, li))
                .OrderBy(li => li.Key, StringComparer.Ordinal)
                .FirstOrDefault(li => WorldMoves.MeetsByChance(
                    saveKey, li.Key, WorldMoves.Where(saveKey, ScheduleFor(saveId, play.Setting, li, flags), settingPlaces, play.Clock), place.Id, play.Clock)) is { } stranger)
        {
            outcome = outcome with
            {
                EncounterId = JsonEncounterCatalog.ChanceMeetingId,
                With = [stranger.Ref],
                Text = $"You run into {stranger.Name} at {place.Name}, and the two of you get talking for the first time.",
                FlagsToSet = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [$"{stranger.Key}.met"] = "true",
                    [$"{stranger.Key}.place"] = place.Id,
                    [$"{stranger.Key}.slot"] = play.Clock.Slot.ToString(),
                    [ChanceMeetingDayKey] = play.Clock.Day.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
                Reveals = [],
                Choices = null,
            };
        }

        // At the workplace during a shift the player is working it, whatever else happens there.
        if (play.Setting.Job is { } job && job.Place == place.Id && PlayerLife.OnShift(job, play.Clock))
        {
            outcome = outcome with { Duty = JobText(job.Scene, place.Name) };
        }

        if (invite is not null && !outcome.With.Contains(RefFor(invite)))
        {
            throw new InvalidOperationException($"Nothing at {place.Name} would bring them along.");
        }

        // A meeting the player agreed to happens when they turn up for it, whatever else was planned
        // for a quiet turn.
        if (outcome.EncounterId is null
            && openPromises.FirstOrDefault(p => Promises.PutsThere(p, play.Clock, place.Id)) is { } meeting
            && cast.FirstOrDefault(li => li.Id.ToString() == meeting.CharacterId && !HasLeft(flags, li)) is { } waitingFor)
        {
            outcome = outcome with
            {
                EncounterId = JsonEncounterCatalog.PromisedMeetingId,
                With = [waitingFor.Ref],
                Text = $"{waitingFor.Name} is waiting at {place.Name}, as agreed.",
            };
        }

        // No encounter: the turn is still a scene (phase-3 plan). Someone whose schedule puts them
        // here is present, the one the player is closest to first; otherwise it is the place itself.
        if (outcome.EncounterId is null)
        {
            var here = new List<(LoveInterest Person, int Affection)>();
            foreach (var li in cast.Where(li => EncounterEvaluator.Holds(flags, $"{li.Key}.met") && !HasLeft(flags, li)))
            {
                if (WorldMoves.Where(saveKey, ScheduleFor(saveId, play.Setting, li, flags), settingPlaces, play.Clock) == place.Id)
                {
                    // At their own home only once the two are friends: nobody drops in on someone they barely know.
                    if (flags.GetValueOrDefault($"{li.Key}.home") == place.Id
                        && (await story.GetRelationshipAsync(saveId, li.Id, ct).ConfigureAwait(false)).Stage < RelationshipStage.Friend)
                    {
                        continue;
                    }

                    here.Add((li, (await story.GetRelationshipAsync(saveId, li.Id, ct).ConfigureAwait(false)).Affection));
                }
            }

            if (here.OrderByDescending(h => h.Affection).Select(h => h.Person).FirstOrDefault() is { } company)
            {
                // Finding someone where their week puts them a second time is learning that part of their week.
                var noticed = new Dictionary<string, string>(outcome.FlagsToSet, StringComparer.Ordinal);
                if (RoutineKnowledge.Match(ScheduleFor(saveId, play.Setting, company, flags), play.Clock, place.Id) is { } usual)
                {
                    RoutineKnowledge.See(noticed, flags, company.Key, usual);
                }

                outcome = outcome with
                {
                    EncounterId = JsonEncounterCatalog.QuietCompanyId,
                    With = [company.Ref],
                    Text = $"{company.Name} is at {place.Name} too.",
                    FlagsToSet = noticed,
                };
            }
            else if (await SeekerAsync(saveId, cast, flags, play.Clock, ct).ConfigureAwait(false) is { } seeker)
            {
                outcome = outcome with
                {
                    EncounterId = JsonEncounterCatalog.InitiativeId,
                    With = [seeker.Ref],
                    Text = $"{seeker.Name} comes to {place.Name} looking for you.",
                    FlagsToSet = new Dictionary<string, string>(outcome.FlagsToSet, StringComparer.Ordinal)
                    {
                        [$"{seeker.Key}.initiative_day"] = play.Clock.Day.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    },
                };
            }
            else
            {
                outcome = outcome with { EncounterId = JsonEncounterCatalog.QuietAloneId };
            }
        }

        var after = new Dictionary<string, string>(flags, StringComparer.Ordinal);
        after.Remove(EncounterEvaluator.InviteKey);
        after.Remove(JsonEncounterCatalog.DateAgreedKey);
        foreach (var (key, value) in outcome.FlagsToSet)
        {
            after[key] = value;
        }

        // Everyone in the scene: a date at a place they like or dislike, and any stage the turn's
        // flags now allow. It all commits with the turn.
        var toSet = new Dictionary<string, string>(outcome.FlagsToSet, StringComparer.Ordinal);
        var relationships = new Dictionary<Guid, RelationshipState>();

        // Being at work for a shift works it; being anywhere else skips it and costs a little.
        var traits = PlayerLife.Traits(flags);
        if (play.Setting.Job is { } shift && PlayerLife.OnShift(shift, play.Clock))
        {
            if (outcome.Duty is not null)
            {
                PlayerLife.WorkShift(toSet, flags, shift);
            }
            else
            {
                PlayerLife.MissShift(toSet, flags);
                outcome = outcome with { Note = $"You skipped your shift at {PlaceName(play.Setting, play.KnownPlaces, shift.Place)}." };
            }
        }

        // How much the player leads with each person, which leaves them less room to take the lead.
        if (invite is not null)
        {
            var invites = int.TryParse(flags.GetValueOrDefault($"{invite}.invites"), out var n) ? n + 1 : 1;
            toSet[$"{invite}.invites"] = invites.ToString(System.Globalization.CultureInfo.InvariantCulture);
            after[$"{invite}.invites"] = toSet[$"{invite}.invites"];
        }
        foreach (var li in cast.Where(li => outcome.With.Contains(li.Ref)))
        {
            var current = await story.GetRelationshipAsync(saveId, li.Id, ct).ConfigureAwait(false);
            if (outcome.EncounterId == JsonEncounterCatalog.FirstDateIdFor(li.Key))
            {
                current = _engine.DateAt(current, li.Profile, place.TypeId, play.Clock.Day);
            }

            // Who the player has become warms the people who value it a little.
            var rapport = PlayerLife.Rapport(li.Profile.WeightOf, traits, storyContent.Rules);
            if (rapport != 0)
            {
                var none = _engine.Score(li.Profile, li.Member.Temper, li.Member.WantId, []);
                current = _engine.Apply(current, none with { Affection = none.Affection + rapport }, play.Clock.Day);
            }

            relationships[li.Id] = Advance(li, current, after, toSet);
        }

        // Promises: a meeting kept by being there together, anything whose time has passed broken.
        // The trust change commits with the turn; the promise is marked once the turn is stored.
        var presentIds = cast.Where(li => outcome.With.Contains(li.Ref)).Select(li => li.Id.ToString()).ToList();
        var resolvedPromises = new List<(Promise Promise, PromiseStatus Status)>();
        foreach (var promise in openPromises)
        {
            if (Promises.Resolve(promise, outcome.VisitedAt, place.Id, presentIds) is { } status
                && cast.FirstOrDefault(li => li.Id.ToString() == promise.CharacterId) is { } promisedTo)
            {
                var current = relationships.GetValueOrDefault(promisedTo.Id)
                    ?? await story.GetRelationshipAsync(saveId, promisedTo.Id, ct).ConfigureAwait(false);
                relationships[promisedTo.Id] = _engine.PromiseResolved(current, promisedTo.Member.Temper, status, outcome.VisitedAt.Day);
                resolvedPromises.Add((promise, status));
            }
        }

        // The daily world tick (plan §7): at the end of each day, evaluate the leaving rules.
        if (outcome.Next.Day != outcome.VisitedAt.Day || outcome.GameOver)
        {
            foreach (var status in await StatusesAsync(saveId, cast, after, relationships, ct).ConfigureAwait(false))
            {
                var seenToday = outcome.With.Contains(RefFor(status.Key));
                var checkedStatus = seenToday ? status with { LastSeenDay = outcome.VisitedAt.Day } : status;

                if (_endings.Leaving(checkedStatus, outcome.VisitedAt.Day) is { } reason)
                {
                    toSet[$"{status.Key}.left"] = reason.ToString();
                    after[$"{status.Key}.left"] = reason.ToString();
                }
            }
        }

        outcome = outcome with { FlagsToSet = toSet };
        await state.CommitTurnAsync(saveId, outcome, relationships, ct).ConfigureAwait(false);

        foreach (var (promise, status) in resolvedPromises)
        {
            await story.ResolvePromiseAsync(saveId, promise.Id, status, outcome.VisitedAt.Day, ct).ConfigureAwait(false);
        }

        var names = await NamesAsync(saveId, cast, ct).ConfigureAwait(false);
        var owner = Owner(cast, outcome.With);
        var filled = outcome with
        {
            Text = Fill(outcome.Text, names, owner, place.Name, outcome.VisitedAt),
            Choices = [.. (outcome.Choices ?? []).Select(c => c with { Text = Fill(c.Text, names, owner, place.Name, outcome.VisitedAt) })],
        };

        // The scene is saved as it happens; its picture, person, words and reply are added as they arrive.
        await sceneLog.StartAsync(
            saveId, filled.VisitedAt, place.Id, filled.EncounterId, TurnOutcomeJson.Serialize(filled), filled.Text, ct).ConfigureAwait(false);

        return filled;
    }

    /// <summary>Everything the game knows about a save, for the hidden debug screen: raw, in sections.</summary>
    public async Task<IReadOnlyList<DebugSection>> DebugReportAsync(SaveId saveId, CancellationToken ct = default)
    {
        var play = await GetPlayStateAsync(saveId, ct).ConfigureAwait(false);
        var flags = await state.GetFlagsAsync(saveId, ct).ConfigureAwait(false);
        var cast = await CastAsync(saveId, play.Setting, ct).ConfigureAwait(false);
        var promises = await story.GetPromisesAsync(saveId, openOnly: false, ct).ConfigureAwait(false);
        var facts = await story.GetFactsAsync(saveId, ct).ConfigureAwait(false);
        var memories = await memoryStore.ListAsync(saveId, ct).ConfigureAwait(false);
        var scenes = await sceneLog.ListAsync(saveId, ct).ConfigureAwait(false);
        var pendingScene = await state.GetPendingSceneAsync(saveId, ct).ConfigureAwait(false);
        string NameOf(string? id) => cast.FirstOrDefault(li => li.Id.ToString() == id || li.Ref == id || li.Key == id)?.Name ?? id ?? "-";

        var sections = new List<DebugSection>
        {
            new("Save",
            [
                $"id: {saveId}",
                $"setting: {play.Setting.Id} ({play.Setting.DisplayName}), {play.Setting.Days} days",
                $"player: {await saves.GetPlayerNameAsync(saveId, ct).ConfigureAwait(false)} ({await saves.GetPlayerGenderAsync(saveId, ct).ConfigureAwait(false) ?? "-"})",
                $"narration: {await saves.GetNarrationLanguageAsync(saveId, ct).ConfigureAwait(false) ?? "English"}",
                $"clock: day {play.Clock.Day} (weekday {CharacterSchedule.Weekday(play.Clock.Day)}), {play.Clock.Slot}",
                $"opening: {play.Opening?.Id ?? "-"}   over: {play.Over}   ending: {(play.Ending is null ? "-" : "reached")}   offer: {(play.EndingOffer is null ? "-" : "open")}",
                $"job: {(play.Job is { } shown ? JobText(shown.Title, PlaceName(play.Setting, play.KnownPlaces, shown.Place)) : "-")}   on shift now: {play.OnShift}",
                $"pending choice: {(play.Pending is null ? "-" : string.Join(" | ", play.Pending.Choices.Select(c => c.Id)))}",
                $"pending scene: {(pendingScene is null ? "-" : $"{pendingScene.EncounterId} with {string.Join(", ", pendingScene.With.Select(NameOf))}, replies {pendingScene.Replies}")}",
            ]),
        };

        var people = new List<string>();
        foreach (var li in cast)
        {
            var relationship = await story.GetRelationshipAsync(saveId, li.Id, ct).ConfigureAwait(false);
            people.Add($"{li.Name} [{li.Key}] {li.Id}");
            people.Add($"  {relationship}");
            people.Add($"  member: {JsonSerializer.Serialize(li.Member, Json)}");
            people.Add($"  profile: {JsonSerializer.Serialize(li.Profile, Json)}");
            var schedule = ScheduleFor(saveId, play.Setting, li, flags);
            var now = WorldMoves.Where(saveId.ToString(), schedule, [.. play.Setting.Places.Select(p => p.Id)], play.Clock);
            people.Add($"  usually at: {schedule.Where(play.Clock) ?? "-"}   now at: {now ?? "-"}   met: {EncounterEvaluator.Holds(flags, $"{li.Key}.met")}   left: {HasLeft(flags, li)}");
            people.Add($"  week: {RoutineKnowledge.Describe(RoutineKnowledge.Entries(schedule), id => PlaceName(play.Setting, play.KnownPlaces, id))}");
            people.Add($"  player knows: {RoutineKnowledge.Describe(KnownRoutine(play.Setting, li, schedule, flags), id => PlaceName(play.Setting, play.KnownPlaces, id))}   home: {flags.GetValueOrDefault($"{li.Key}.home") ?? "-"}");
            people.Add($"  voice: {await story.GetVoiceAsync(li.Id, ct).ConfigureAwait(false) ?? "-"}");
            people.Add($"  rapport now:{PlayerLife.Rapport(li.Profile.WeightOf, PlayerLife.Traits(flags), storyContent.Rules)}");
        }

        sections.Add(new("People", people));
        sections.Add(new("Player traits",
        [
            .. PlayerLife.Traits(flags).OrderByDescending(t => t.Value)
                .Select(t => $"{t.Key}: {t.Value} (level {PlayerLife.Level(t.Value, storyContent.Rules.TraitLevels)})"),
            .. await PlayerLifeLinesAsync(saveId, play.Setting, play.KnownPlaces, "the player", ct).ConfigureAwait(false),
        ]));
        sections.Add(new("Hints", play.Hints));
        sections.Add(new("Loose ends",
        [
            .. (await threads.ListAsync(saveId, openOnly: false, ct).ConfigureAwait(false)).Select(t =>
                $"#{t.Id} day {t.OpenedDay}{(t.ClosedDay is { } closed ? $", settled day {closed}" : "")} about {NameOf(t.CharacterId)}: {t.Text}"),
        ]));
        sections.Add(new("Flags", [.. flags.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => $"{f.Key} = {f.Value}")]));
        sections.Add(new("Promises",
        [
            .. promises.Select(p => $"{p.Status} {p.Kind} with {NameOf(p.CharacterId)}: made day {p.MadeDay}, due day {p.DueDay} {p.DueSlot?.ToString() ?? ""} at {p.PlaceId ?? "-"}"),
        ]));
        sections.Add(new("Places",
        [
            .. play.KnownPlaces.Select(p => $"{p.Id} ({p.TypeId}) {p.Name}{(p.Details.Count > 0 ? $" [{string.Join(", ", p.Details)}]" : "")}{(p.Look is null ? "" : $" look: {p.Look}")}"),
        ]));
        sections.Add(new("Facts",
        [
            .. facts.Select(f => $"#{f.Id} {NameOf(f.Fact.Subject)} {f.Fact.Predicate} {f.Fact.Object} [{f.Fact.Level}, day {f.Fact.Day}, {f.Fact.Source}] known by {string.Join(", ", f.Knowers.Select(NameOf))}"),
        ]));
        sections.Add(new("Memories",
        [
            .. memories.Select(m => $"#{m.Id} day {m.Day} {m.Scope}{(m.CompactedInto is null ? "" : $" (in #{m.CompactedInto})")}: {m.Summary}"),
        ]));
        sections.Add(new("Scenes",
        [
            .. scenes.AsEnumerable().Reverse().SelectMany(s => (IEnumerable<string>)
            [
                $"#{s.Id} day {s.Clock.Day} {s.Clock.Slot} at {s.PlaceId}: {s.EncounterId ?? "-"} with {s.Speaker ?? "-"} ({(s.Written ? "written" : "fallback")}), {s.Exchanges.Count} replies",
                $"  background: {s.BackgroundPath ?? "-"}",
                $"  sprite: {s.SpritePath ?? "-"} ({s.Expression ?? "-"})",
                $"  {s.Text}",
                .. s.Exchanges.Select(e => $"  > {e.Reply}{(e.Agreed is null ? "" : $" [agreed: {e.Agreed}]")}\n    {e.Reaction}"),
            ]),
        ]));

        return sections;
    }

    /// <summary>
    /// Texts <paramref name="key"/>, someone whose number the player has, from the place of the scene on screen, once
    /// its conversation is over (user feedback: texting took the slot at home, drawn as a scene in the stairwell). It
    /// takes no time of its own: it is the rest of the slot already spent there, so one a slot, and short. Starts the
    /// conversation's scene at the same place and slot, closing the one it follows; the clock does not move.
    /// </summary>
    public async Task<TurnOutcome> TextAsync(SaveId saveId, string key, CancellationToken ct = default)
    {
        var open = await sceneLog.GetOpenAsync(saveId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Texting happens wherever the player is: go somewhere first.");

        if (open.EncounterId == JsonEncounterCatalog.PhoneId)
        {
            throw new InvalidOperationException("There is time for one conversation by text a slot.");
        }

        if (MeetingAgreement.Heading(await story.GetPromisesAsync(saveId, openOnly: true, ct).ConfigureAwait(false), open.Clock.Next()) is not null)
        {
            throw new InvalidOperationException("You are setting off somewhere together: there is no time to text anyone.");
        }

        if (!open.Written)
        {
            throw new InvalidOperationException("The scene is still being written.");
        }

        var setting = await EnsureSettingAsync(saveId, ct).ConfigureAwait(false);
        var flags = await state.GetFlagsAsync(saveId, ct).ConfigureAwait(false);
        if (EncounterEvaluator.Holds(flags, EncounterEvaluator.PendingChoiceKey))
        {
            throw new InvalidOperationException("Answer the open choice first.");
        }

        var cast = await CastAsync(saveId, setting, ct).ConfigureAwait(false);
        var texted = cast.FirstOrDefault(li => li.Key == key && EncounterEvaluator.Holds(flags, $"{li.Key}.contact") && !HasLeft(flags, li))
            ?? throw new InvalidOperationException($"'{key}' is not someone the player can message.");

        var here = TurnOutcomeJson.Deserialize(open.OutcomeJson);
        if (here.With.Contains(texted.Ref))
        {
            throw new InvalidOperationException($"{texted.Name} is right here.");
        }

        // Leaving the scene's conversation as Continue does, anything unsaid unsaid.
        await CloseSceneAsync(saveId, ct).ConfigureAwait(false);

        var phone = new TurnOutcome(
            open.Clock,
            here.Next,
            open.PlaceId,
            JsonEncounterCatalog.PhoneId,
            $"{texted.Name} and you are texting.",
            new Dictionary<string, string>(StringComparer.Ordinal),
            [],
            [texted.Ref],
            here.GameOver);

        await sceneLog.StartAsync(saveId, phone.VisitedAt, phone.PlaceId, phone.EncounterId, TurnOutcomeJson.Serialize(phone), phone.Text, ct).ConfigureAwait(false);
        return phone;
    }

    /// <summary>A planned beat at or below this priority gives way to running into someone by chance.</summary>
    private const int ChanceMeetingPriority = 60;

    /// <summary>The day of the last chance meeting: one a day at most.</summary>
    private const string ChanceMeetingDayKey = "chance_meeting.day";

    /// <summary>
    /// What the writer is told about the player's life: their job, and what they chose to do lately on their own,
    /// from the scene log, so the people they meet can bring it up.
    /// </summary>
    private async Task<IReadOnlyList<string>> PlayerLifeLinesAsync(
        SaveId saveId, SettingDefinition setting, IReadOnlyList<PlaceRecord> known, string player, CancellationToken ct)
    {
        var lines = new List<string>();
        if (setting.Job is { } job)
        {
            lines.Add($"{player} {JobText(job.Habit, PlaceName(setting, known, job.Place))}.");
        }

        // So an invitation over has somewhere to name: an agreed meeting there brings someone to the player's place.
        lines.Add($"{player} lives at {PlaceName(setting, known, setting.Home ?? setting.RoutinePlace)}.");

        var lately = (await sceneLog.ListAsync(saveId, ct).ConfigureAwait(false))
            .Where(s => s.EncounterId == JsonEncounterCatalog.QuietAloneId && s.Exchanges.Count > 0)
            .Reverse()
            .SelectMany(s => s.Exchanges.Take(1).Select(e => $"\"{e.Reply}\" at {PlaceName(setting, known, s.PlaceId)}"))
            .Take(3)
            .ToList();
        if (lately.Count > 0)
        {
            lines.Add($"What {player} has done lately on their own: {string.Join("; ", lately)}.");
        }

        return lines;
    }

    /// <summary>
    /// Answers the open choice. Its tags are scored for everyone in the scene, and the person the scene is
    /// about answers it like any reply, so the conversation can go on. Null when nobody is there to answer.
    /// </summary>
    public async Task<ReactionResult?> ChooseAsync(SaveId saveId, string choiceId, CancellationToken ct = default)
    {
        var play = await GetPlayStateAsync(saveId, ct).ConfigureAwait(false);

        var pending = play.Pending
            ?? throw new InvalidOperationException("There is no open choice.");

        var choice = pending.Choices.FirstOrDefault(c => c.Id == choiceId)
            ?? throw new InvalidOperationException($"'{choiceId}' is not an answer to the open choice.");

        var encounter = Encounter(saveId, play.Setting, pending.EncounterId);
        var cast = await CastAsync(saveId, play.Setting, ct).ConfigureAwait(false);
        var sets = new Dictionary<string, string>(TurnPlanner.Assignments(choice.Sets ?? []), StringComparer.Ordinal);

        var after = new Dictionary<string, string>(await state.GetFlagsAsync(saveId, ct).ConfigureAwait(false), StringComparer.Ordinal);
        foreach (var (key, value) in sets)
        {
            after[key] = value;
        }

        // Arc choices name "the want of the person this scene is about"; score them as that want.
        var ownerWant = Owner(cast, encounter.With ?? [])?.Member.WantId ?? "";
        var tags = (choice.Tags ?? []).Select(t => t.Replace(StoryContent.WantToken, ownerWant, StringComparison.Ordinal)).ToList();

        var relationships = new Dictionary<Guid, RelationshipState>();
        var before = new Dictionary<Guid, RelationshipState>();
        var effects = new List<ChoiceEffect>();
        foreach (var li in cast.Where(li => (encounter.With ?? []).Contains(li.Ref)))
        {
            var current = await story.GetRelationshipAsync(saveId, li.Id, ct).ConfigureAwait(false);
            before[li.Id] = current;
            var delta = _engine.Score(li.Profile, li.Member.Temper, li.Member.WantId, tags);
            relationships[li.Id] = Advance(li, _engine.Apply(current, delta, play.Clock.Day), after, sets);
            effects.Add(Effect(li.Name, current, relationships[li.Id]));
        }

        await state.ResolveChoiceAsync(saveId, pending.EncounterId, choice.Id, sets, relationships, ct).ConfigureAwait(false);
        await story.LogTurnAsync(saveId, play.Clock, ChoiceLogKind,
            new ChoiceRecord(play.Clock.Day, play.Clock.Slot.ToString(), choice.Text, effects), ct).ConfigureAwait(false);

        // The person the scene is about answers, as for any reply, and the conversation can go on from here.
        // The choice's tags are scored above, so the reaction scores nothing more.
        var owner = Owner(cast, encounter.With ?? []);
        if (owner is null || await sceneLog.GetOpenAsync(saveId, ct).ConfigureAwait(false) is not { } open)
        {
            return null;
        }

        var popup = relationships.TryGetValue(owner.Id, out var changed) ? ReactionPopup.For(owner.Name, before[owner.Id], changed) : null;
        var moment = new PendingScene(open.Clock, open.PlaceId, pending.EncounterId, encounter.With ?? [], open.Text, []);
        await state.SavePendingSceneAsync(saveId, moment, ct).ConfigureAwait(false);

        return await RespondCoreAsync(saveId, moment, choice.Text, chosenTags: [], logChoice: false, choicePopup: popup, rewrite: false, ct).ConfigureAwait(false);
    }

    /// <summary>The turn-log kind every choice the player makes is recorded under, for the ending's recap.</summary>
    public const string ChoiceLogKind = "player-choice";

    private static ChoiceEffect Effect(string name, RelationshipState before, RelationshipState after) =>
        new(name, after.Affection - before.Affection, after.Trust - before.Trust, after.Dealbreaker && !before.Dealbreaker);

    /// <summary>
    /// Ends the story with <paramref name="pick"/>: a route key on offer, or <see cref="EndingRules.AloneKey"/>.
    /// Stores the recap once; a save that has already ended refuses.
    /// </summary>
    public async Task EndAsync(SaveId saveId, string pick, CancellationToken ct = default)
    {
        var play = await GetPlayStateAsync(saveId, ct).ConfigureAwait(false);

        if (play.Ending is not null)
        {
            throw new InvalidOperationException("This story has already ended.");
        }

        var offer = play.EndingOffer
            ?? throw new InvalidOperationException("The ending check is not due yet.");

        var flags = await state.GetFlagsAsync(saveId, ct).ConfigureAwait(false);
        var cast = await CastAsync(saveId, play.Setting, ct).ConfigureAwait(false);
        var statuses = await StatusesAsync(saveId, cast, flags, null, ct).ConfigureAwait(false);
        var choice = EndingRules.Resolve(statuses, pick);

        var partner = choice.Key is null ? null : cast.First(li => li.Key == choice.Key);
        var names = await NamesAsync(saveId, cast, ct).ConfigureAwait(false);

        var stillAround = statuses.Where(s => s.Open).ToList();
        var closest = partner ?? stillAround
            .OrderByDescending(s => s.State.Affection)
            .Select(s => cast.First(li => li.Key == s.Key))
            .FirstOrDefault();

        var text = choice.Kind switch
        {
            EndingKind.Together => endingContent.Texts.Together,
            EndingKind.Alone => endingContent.Texts.Alone,
            _ => endingContent.Texts.LeftAlone,
        };

        var passedOver = stillAround.Where(s => s.Key != choice.Key).Select(s => cast.First(li => li.Key == s.Key).Name).ToList();

        // The choices that mattered, as the player made them (phase-3 plan: the recap).
        var choices = ChoiceRecap.For(
            (await story.ListTurnsAsync(saveId, ChoiceLogKind, ct).ConfigureAwait(false))
                .Select(t => JsonSerializer.Deserialize<ChoiceRecord>(t.PayloadJson, Json))
                .OfType<ChoiceRecord>());

        // Gemma tells the ending C# decided; the authored text stands in when it cannot.
        var ends = castContent.Temper.SelectMany(a => a.Ends).ToDictionary(e => e.Id, e => e.Writing, StringComparer.Ordinal);
        var memories = (await memoryStore.ListAsync(saveId, ct).ConfigureAwait(false))
            .Where(m => m.CompactedInto is null)
            .OrderBy(m => m.Day)
            .TakeLast(10)
            .Select(m => $"Day {m.Day}: {m.Summary}")
            .ToList();
        var ceiling = (await saves.ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(s => s.Id == saveId)?.Ceiling ?? Ceiling.PG13;

        var epilogue = await epilogueWriter.WriteAsync(
            new EpilogueRequest(
                play.Setting.DisplayName,
                play.Setting.Tone,
                names.Player,
                choice.Kind,
                partner?.Name,
                partner is null ? [] : [.. partner.Member.Temper.Values.Select(end => ends.GetValueOrDefault(end, "")).Where(w => w.Length > 0)],
                passedOver,
                offer.Departures,
                memories,
                choices,
                ceiling,
                Language: await saves.GetNarrationLanguageAsync(saveId, ct).ConfigureAwait(false),
                PlayerGender: await saves.GetPlayerGenderAsync(saveId, ct).ConfigureAwait(false)),
            Fill(text, names, partner, "", play.Clock),
            ct).ConfigureAwait(false);

        await story.LogTurnAsync(saveId, play.Clock, epilogue.Fallback ? "epilogue-fallback" : "epilogue", new
        {
            epilogue.Text,
            epilogue.Attempts,
            epilogue.Rejections,
        }, ct).ConfigureAwait(false);

        var recap = new EndingRecap(
            choice.Kind,
            epilogue.Text,
            partner?.Name,
            passedOver,
            offer.Departures,
            closest is null ? null : partner is null ? $"{closest.Name}, the one you were closest to" : closest.Name,
            closest is null ? [] : ProfileLines(closest),
            choices);

        await story.SaveEndingAsync(
            saveId,
            new StoredEnding(choice.Kind, partner?.Id, Math.Min(play.Clock.Day, play.Setting.Days), JsonSerializer.Serialize(recap, Json)),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A picture of someone the player can invite: their full-body scene sprite at their temper's
    /// resting expression, the same image the scenes use, so it is rendered once and cached.
    /// </summary>
    public async Task<string?> PortraitAsync(SaveId saveId, string key, CancellationToken ct = default)
    {
        var setting = await EnsureSettingAsync(saveId, ct).ConfigureAwait(false);
        var li = (await CastAsync(saveId, setting, ct).ConfigureAwait(false)).FirstOrDefault(l => l.Key == key);

        return li is null
            ? null
            : await studio.GenerateSceneSpriteAsync(saveId, li.Id, li.Member.Aesthetic, li.Member.RestingExpression(castContent)).ConfigureAwait(false);
    }

    /// <summary>
    /// Who a taken turn shows before anything is written: the person the scene is about, at their temper's resting
    /// expression, and on the stage only those there from the start, so nobody is drawn before the words bring them in.
    /// </summary>
    public async Task<SceneView> PresentAsync(SaveId saveId, TurnOutcome outcome, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        var setting = await EnsureSettingAsync(saveId, ct).ConfigureAwait(false);
        var cast = await CastAsync(saveId, setting, ct).ConfigureAwait(false);

        if (Owner(cast, outcome.With) is not { } owner)
        {
            return new SceneView(outcome.Text, null, null, null, null);
        }

        var pack = await studio.GetPackAsync(ct).ConfigureAwait(false);
        var expression = ScenePresentation.Expression(null, owner.Member.RestingExpression(castContent), [.. pack.Expressions.Keys]);

        return new SceneView(outcome.Text, owner.Id, owner.Name, owner.Member.Aesthetic, expression)
        {
            Figures = outcome.EncounterId == JsonEncounterCatalog.PhoneId ? [] : Stage.Opening(Candidates(cast, outcome.AtFirst), [.. pack.Expressions.Keys]),
        };
    }

    /// <summary>Everyone a scene is with who could stand on its stage, in the encounter's order.</summary>
    private IReadOnlyList<StageCandidate> Candidates(IReadOnlyList<LoveInterest> cast, IReadOnlyList<string> with) =>
        [
            .. with.Select(w => cast.FirstOrDefault(li => li.Ref == w)).OfType<LoveInterest>()
                .Select(li => new StageCandidate(li.Id, li.Name, li.Member.Aesthetic, li.Member.RestingExpression(castContent))),
        ];

    /// <summary>The stage after a block of words, with each figure's outfit and any picture already drawn kept.</summary>
    /// <param name="wearing">What each person on the stage now wears, where the words settled it.</param>
    private async Task<IReadOnlyList<SceneFigure>> StageAfterAsync(
        IReadOnlyList<LoveInterest> cast,
        IReadOnlyList<string> with,
        IReadOnlyList<SceneFigure> shown,
        IReadOnlyList<Presence>? present,
        LoveInterest? owner,
        string? ownerExpression,
        bool firstWords,
        IReadOnlyDictionary<Guid, Outfit> wearing,
        CancellationToken ct)
    {
        var pack = await studio.GetPackAsync(ct).ConfigureAwait(false);
        var figures = Stage.After(Candidates(cast, with), shown, present, owner?.Id, ownerExpression, firstWords, [.. pack.Expressions.Keys]);

        return [.. figures.Select(f => wearing.TryGetValue(f.CharacterId, out var outfit) && outfit != f.Outfit ? f with { Outfit = outfit, SpritePath = null } : f)];
    }

    /// <summary>The sprite of the person a scene shows, rendered on first use. Null when it shows no one.</summary>
    public async Task<string?> SpriteAsync(SaveId saveId, SceneView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        return view is { CharacterId: { } id, Expression: { } expression }
            ? await studio.GenerateSceneSpriteAsync(saveId, id, view.Aesthetic ?? "", expression).ConfigureAwait(false)
            : null;
    }

    /// <summary>
    /// The open scene's background, drawn at the slot and weather of the visit and saved with the scene,
    /// so coming back shows the same picture without drawing it again. Null when no scene is open.
    /// </summary>
    public async Task<string?> SceneBackgroundAsync(SaveId saveId, CancellationToken ct = default)
    {
        var scene = await sceneLog.GetOpenAsync(saveId, ct).ConfigureAwait(false);
        if (scene is null)
        {
            return null;
        }

        if (scene.BackgroundPath is { } saved)
        {
            return saved;
        }

        var setting = await EnsureSettingAsync(saveId, ct).ConfigureAwait(false);
        var place = await places.GetAsync(saveId, scene.PlaceId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The scene's place '{scene.PlaceId}' is not in this save.");

        var path = await studio.GenerateBackgroundAsync(saveId, place, scene.Clock.Slot, WeatherOn(saveId, setting, scene.Clock.Day).Id).ConfigureAwait(false);
        await sceneLog.SetBackgroundAsync(scene.Id, path, ct).ConfigureAwait(false);
        return path;
    }

    /// <summary>
    /// Everyone on the scene's stage, drawn and saved with the open scene, in the order of <see cref="SceneView.Figures"/>.
    /// Each is drawn on its own: a null path is someone who could not be drawn, and the others are still shown.
    /// A picture already drawn at the same expression and outfit is not drawn again.
    /// </summary>
    public async Task<IReadOnlyList<string?>> SceneSpriteAsync(SaveId saveId, SceneView view, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(view);

        // Taken before drawing: the player may have moved on by the time the picture is ready.
        var scene = await sceneLog.GetOpenAsync(saveId, ct).ConfigureAwait(false);

        var paths = new List<string?>();
        foreach (var figure in view.Figures)
        {
            if (figure.SpritePath is { } drawn)
            {
                paths.Add(drawn);
                continue;
            }

            try
            {
                var (dress, layer, garments) = scene is null ? (DressCode.Casual, null, null) : await WardrobeForAsync(saveId, scene, figure, ct).ConfigureAwait(false);
                paths.Add(await studio.GenerateSceneSpriteAsync(saveId, figure.CharacterId, figure.Aesthetic ?? "", figure.Expression, dress, layer, garments).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                paths.Add(null);
            }
        }

        if (scene is null)
        {
            return paths;
        }

        // Pictures land on the stage as it is now: a later block of words may already have changed who stands there.
        var now = await sceneLog.GetOpenAsync(saveId, ct).ConfigureAwait(false);
        if (now?.Id != scene.Id)
        {
            return paths;
        }

        var stage = now.Written ? now.Figures ?? [] : view.Figures;
        var drawnNow = view.Figures.Zip(paths).Where(p => p.Second is not null).ToList();
        IReadOnlyList<SceneFigure> kept =
        [
            .. stage.Select(f => drawnNow.FirstOrDefault(d => d.First.CharacterId == f.CharacterId && d.First.Expression == f.Expression && d.First.Outfit == f.Outfit)
                is { Second: { } path } ? f with { SpritePath = path } : f),
        ];
        await sceneLog.SetFiguresAsync(scene.Id, kept, ct).ConfigureAwait(false);

        // The older columns keep the person the scene is about, as far as the stage shows them.
        if (kept.FirstOrDefault(f => f.CharacterId == view.CharacterId) is { } about)
        {
            await sceneLog.SetPersonAsync(scene.Id, about.CharacterId, about.Name, about.Expression, about.SpritePath, ct).ConfigureAwait(false);
        }

        return paths;
    }

    /// <summary>
    /// Finishes writing the open scene when leaving the game interrupted it. Null when no scene is open
    /// or it is already written.
    /// </summary>
    public async Task<SceneView?> ResumeSceneAsync(SaveId saveId, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var scene = await sceneLog.GetOpenAsync(saveId, ct).ConfigureAwait(false);
        return scene is null || scene.Written
            ? null
            : await WriteSceneAsync(saveId, TurnOutcomeJson.Deserialize(scene.OutcomeJson), progress, ct).ConfigureAwait(false);
    }

    /// <summary>The player has moved on from the scene they were in (Continue), leaving any reply unsaid.</summary>
    public async Task CloseSceneAsync(SaveId saveId, CancellationToken ct = default)
    {
        if (await state.GetPendingSceneAsync(saveId, ct).ConfigureAwait(false) is not null)
        {
            await state.ResolvePendingSceneAsync(
                saveId, new Dictionary<Guid, RelationshipState>(), new Dictionary<string, string>(StringComparer.Ordinal), ct).ConfigureAwait(false);
        }

        await sceneLog.CloseAsync(saveId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// What someone in a scene wears (user feedback: the same outfit everywhere looked wrong): what the scene or its
    /// conversation said, or else what suits the moment, with a weather layer outdoors unless something is worn over it.
    /// </summary>
    private async Task<(string Dress, string? Layer, string? Garments)> WardrobeForAsync(SaveId saveId, StoredScene scene, SceneFigure figure, CancellationToken ct)
    {
        var characterId = figure.CharacterId;
        var setting = await EnsureSettingAsync(saveId, ct).ConfigureAwait(false);
        var outfit = figure.Outfit ?? (scene.CharacterId == characterId ? scene.Outfit : null);
        if (outfit is null)
        {
            var cast = await CastAsync(saveId, setting, ct).ConfigureAwait(false);
            outfit = cast.FirstOrDefault(li => li.Id == characterId) is { } li
                ? (await OutfitOptionsAsync(saveId, setting, cast, li, scene.Clock, scene.PlaceId, scene.EncounterId, ct).ConfigureAwait(false)).Wearing
                : new Outfit(DressCode.Casual);
        }

        if (outfit.Over is not null || outfit.Dress is DressCode.Swim
            || await places.GetAsync(saveId, scene.PlaceId, ct).ConfigureAwait(false) is not { } place)
        {
            return (outfit.Dress, outfit.Over, outfit.Garments);
        }

        return (outfit.Dress, placeTypes.Get(place.TypeId).OutfitLayers?.GetValueOrDefault(WeatherOn(saveId, setting, scene.Clock.Day).Id), outfit.Garments);
    }

    /// <summary>
    /// What <paramref name="li"/> can be wearing at a place and time (user feedback: a swimsuit on the pier every time,
    /// and no time to change on the way to the lookout together): what they wore with the player in the slot just before,
    /// or else the place's clothes, with what suited where their day had them before.
    /// </summary>
    private async Task<PacketOutfit> OutfitOptionsAsync(
        SaveId saveId, SettingDefinition setting, IReadOnlyList<LoveInterest> cast, LoveInterest li, ClockState clock, string placeId, string? encounterId,
        CancellationToken ct)
    {
        var place = await places.GetAsync(saveId, placeId, ct).ConfigureAwait(false);
        var placeDress = place is null ? DressCode.Casual : placeTypes.Get(place.TypeId).Dress;
        var firstDate = encounterId is { } id && cast.Any(c => JsonEncounterCatalog.FirstDateIdFor(c.Key) == id);

        // At the player's own home the home clothes are the player's. Someone who came over is a guest,
        // and came dressed to be somewhere, so they wear what they would anywhere else.
        if (placeDress is DressCode.Home && string.Equals(place?.Id, setting.Home, StringComparison.Ordinal))
        {
            placeDress = DressCode.Casual;
        }

        // Brought here by agreeing to go together straight away, overnight too: no time to change since the scene they
        // set off from (user request: going now and meeting later are different). Seeing them again otherwise starts afresh.
        static int Order(ClockState c) => (c.Day * 10) + (int)c.Slot;
        var cameWith = (await story.GetPromisesAsync(saveId, openOnly: false, ct).ConfigureAwait(false))
            .Any(p => MeetingAgreement.IsNow(p) && p.CharacterId == li.Id.ToString() && p.PlaceId == placeId
                      && Promises.IsDue(p, clock) && p.Status is not PromiseStatus.Broken);
        var together = cameWith
            ? (await sceneLog.ListAsync(saveId, ct).ConfigureAwait(false))
                .LastOrDefault(s => (s.CharacterId == li.Id || (s.Figures ?? []).Any(f => f.CharacterId == li.Id))
                                    && Order(s.Clock) < Order(clock) && s.EncounterId != JsonEncounterCatalog.PhoneId)
            : null;
        if (together is not null)
        {
            var kept = (together.Figures ?? []).FirstOrDefault(f => f.CharacterId == li.Id)?.Outfit
                ?? (together.CharacterId == li.Id ? together.Outfit : null)
                ?? (await OutfitOptionsAsync(saveId, setting, cast, li, together.Clock, together.PlaceId, together.EncounterId, ct).ConfigureAwait(false)).Wearing;
            var there = together.PlaceId == placeId ? null : (await places.GetAsync(saveId, together.PlaceId, ct).ConfigureAwait(false))?.Name;
            return Outfits.For(li.Name, placeDress, firstDate, kept, null, there);
        }

        // Mornings start from home, where they got ready for the day.
        if (clock.Slot is TimeOfDay.Morning)
        {
            return Outfits.For(li.Name, placeDress, firstDate, null, null, null);
        }

        var before = new ClockState(clock.Day, clock.Slot - 1);
        var flags = await state.GetFlagsAsync(saveId, ct).ConfigureAwait(false);
        var fromId = WorldMoves.Where(saveId.ToString(), ScheduleFor(saveId, setting, li, flags), [.. setting.Places.Select(p => p.Id)], before);
        var from = fromId is null || fromId == placeId ? null : await places.GetAsync(saveId, fromId, ct).ConfigureAwait(false);
        return Outfits.For(li.Name, placeDress, firstDate, null, from is null ? null : placeTypes.Get(from.TypeId).Dress, from?.Name);
    }

    /// <summary>
    /// Writes the scene for a turn that has been taken (plan §8), when an LLM is configured. The packet
    /// holds only what the player and the people present know; accepted facts are stored and known by
    /// everyone present; the packet and the answer are logged so the turn can be replayed. Returns the
    /// scene text, or the encounter's authored text when there is no model or no answer passed.
    /// </summary>
    /// <param name="progress">Told each slow step as it starts, in words for the player (getting to know people, remembering, writing).</param>
    public async Task<SceneView> WriteSceneAsync(SaveId saveId, TurnOutcome outcome, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        var presented = await PresentAsync(saveId, outcome, ct).ConfigureAwait(false);

        // The scene row this turn started; the words are saved with it once written.
        var sceneId = await sceneLog.GetOpenAsync(saveId, ct).ConfigureAwait(false) is { } open
                      && open.Clock == outcome.VisitedAt
                      && open.PlaceId == outcome.PlaceId
            ? open.Id
            : (long?)null;

        if (!llmOptions.Value.Enabled || outcome.EncounterId is null)
        {
            // The authored words bring in everyone the encounter is with.
            var unwritten = presented;
            if (presented.Figures.Count > 0)
            {
                var cast = await CastAsync(saveId, await EnsureSettingAsync(saveId, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                unwritten = presented with
                {
                    Figures = await StageAfterAsync(
                        cast, outcome.With, presented.Figures, null, Owner(cast, outcome.With), null, true, new Dictionary<Guid, Outfit>(), ct).ConfigureAwait(false),
                };
            }

            if (sceneId is { } unwrittenId)
            {
                await sceneLog.SetFiguresAsync(unwrittenId, unwritten.Figures, ct).ConfigureAwait(false);
                await sceneLog.SetWrittenAsync(unwrittenId, unwritten.Text, unwritten.Expression, false, ct).ConfigureAwait(false);
            }

            return unwritten;
        }

        var built = await BuildScenePacketAsync(saveId, outcome, presented, looseEndsOverride: null, ct, progress).ConfigureAwait(false);
        var (known, packet, world, memoryLines) = (built.Known, built.Packet, built.World, built.MemoryLines);
        var presentIds = packet.Present.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var pack = await studio.GetPackAsync(ct).ConfigureAwait(false);
        var day = outcome.VisitedAt.Day;

        progress?.Report("Writing the scene…");
        var written = await sceneWriter.WriteAsync(packet, world, outcome.Text, [.. known.Select(p => p.Name)], packet.OffersChoices, ct).ConfigureAwait(false);

        // Summarising and folding memories asks the model again, before the words are handed back.
        progress?.Report("Keeping it in memory…");

        if (!written.Fallback && packet.LooseEnds is not null)
        {
            await KeepThreadsAsync(saveId, Owner(built.Cast, outcome.With)?.Id, written.Threads, written.Resolved, day, ct).ConfigureAwait(false);
        }

        foreach (var fact in written.Facts)
        {
            await story.AddFactAsync(saveId, fact.Fact, storyContent.Predicate(fact.Fact.Predicate), fact.Knowers, fact.ExplainedBy, ct).ConfigureAwait(false);
        }

        // Places the scene named, homes among them, and what people said about their weeks.
        var present = built.Cast.Where(li => outcome.With.Contains(li.Ref)).ToList();
        var learned = await AddPlacesAsync(saveId, written.Fallback ? [] : written.Places, present, day, ct).ConfigureAwait(false);
        if (!written.Fallback)
        {
            await LearnRoutinesAsync(saveId, present, written.Routines, learned, day, ct).ConfigureAwait(false);
        }

        if (learned.Count > 0)
        {
            await state.SetFlagsAsync(saveId, learned, ct).ConfigureAwait(false);
        }

        await StoreWrittenSceneAsync(saveId, outcome, written, presentIds, ct).ConfigureAwait(false);

        // Kept with the scene: the sprite is drawn in it, and the next slot spent together starts from it.
        if (sceneId is { } dressedId && packet.Outfit is { } offered)
        {
            await sceneLog.SetOutfitAsync(dressedId, written.Outfit ?? offered.Wearing, ct).ConfigureAwait(false);
        }

        await story.LogTurnAsync(saveId, outcome.VisitedAt, written.Fallback ? "scene-fallback" : "scene", new
        {
            outcome.EncounterId,
            Packet = ScenePacketBuilder.Render(packet),
            written.Text,
            written.Expression,
            written.Outfit,
            written.Summary,
            written.Tags,
            Memories = memoryLines,
            Places = written.Places.Select(p => $"{p.Type}: {p.Name}"),
            written.Choices,
            written.Threads,
            written.Resolved,
            written.Attempts,
            written.Rejections,
        }, ct).ConfigureAwait(false);

        // A scene without authored choices waits for the player: a proposed reply or their own words, or Continue
        // when nothing was proposed (user feedback: no options should still leave the text box).
        IReadOnlyList<ProposedChoice> choices = !written.Fallback && packet.OffersChoices ? written.Choices ?? [] : [];
        if (packet.OffersChoices)
        {
            await state.SavePendingSceneAsync(
                saveId,
                new PendingScene(outcome.VisitedAt, outcome.PlaceId, outcome.EncounterId, outcome.With, written.Text, choices),
                ct).ConfigureAwait(false);
        }

        // Who the words brought in and did not see leave; what each of them wears.
        var owner = Owner(built.Cast, outcome.With);
        var wearing = new Dictionary<Guid, Outfit>(built.Wearing);
        if (owner is not null && packet.Outfit is { } dressed)
        {
            wearing[owner.Id] = written.Outfit ?? dressed.Wearing;
        }

        var result = presented with
        {
            Text = written.Text,
            Expression = presented.Expression is null
                ? null
                : ScenePresentation.Expression(written.Expression, presented.Expression, [.. pack.Expressions.Keys]),
            Choices = choices,
            Open = packet.OffersChoices,
            Fallback = written.Fallback,
            Figures = presented.Figures.Count == 0 && packet.Drawn is null
                ? []
                : await StageAfterAsync(built.Cast, outcome.With, presented.Figures, written.Present, owner, written.Expression, true, wearing, ct).ConfigureAwait(false),
        };

        // Marked written last, so an interrupted scene is written again rather than left half-done.
        if (sceneId is { } writtenId)
        {
            await sceneLog.SetFiguresAsync(writtenId, result.Figures, ct).ConfigureAwait(false);
            await sceneLog.SetWrittenAsync(writtenId, result.Text, result.Expression, written.Fallback, ct).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>The scene's memory, kept once it is written.</summary>
    private async Task StoreWrittenSceneAsync(SaveId saveId, TurnOutcome outcome, WrittenScene written, IReadOnlyCollection<string> presentIds, CancellationToken ct)
    {
        var day = outcome.VisitedAt.Day;

        if (!written.Fallback && written.Summary is { } summary)
        {
            await memoryStore.AddAsync(
                saveId,
                new MemoryEntry(0, MemoryScope.Scene, day, summary, [.. presentIds], written.Tags ?? [], await EmbedAsync(summary, ct).ConfigureAwait(false)),
                ct).ConfigureAwait(false);

            foreach (var group in MemoryRetrieval.CompactionGroups(await memoryStore.ListAsync(saveId, ct).ConfigureAwait(false), day))
            {
                var folded = await compactor.SummariseAsync(group.Members, useModel: true, ct).ConfigureAwait(false);
                var people = group.Members.SelectMany(m => m.People).Distinct(StringComparer.Ordinal).ToList();

                await memoryStore.CompactAsync(
                    saveId,
                    new MemoryEntry(0, group.Into, group.Day, folded, people, [], await EmbedAsync(folded, ct).ConfigureAwait(false)),
                    [.. group.Members.Select(m => m.Id)],
                    ct).ConfigureAwait(false);
            }
        }
    }

    /// <param name="Known">Places the player knows.</param>
    /// <param name="MemoryLines">What the packet remembers, kept for the turn log.</param>
    /// <param name="Wearing">What everyone drawn here other than the person the scene is about wears.</param>
    private sealed record BuiltScene(
        IReadOnlyList<LoveInterest> Cast, IReadOnlyList<PlaceRecord> Known, ScenePacket Packet, SceneWorld World, List<string> MemoryLines,
        IReadOnlyDictionary<Guid, Outfit> Wearing);

    /// <summary>
    /// Everything the writer is given for a turn's scene, with the quality material that is switched on: each
    /// person's voice, a small happening at the place, and the loose ends to pick up.
    /// </summary>
    /// <param name="looseEndsOverride">Loose ends to use instead of the save's own, for replaying a past scene.</param>
    private async Task<BuiltScene> BuildScenePacketAsync(
        SaveId saveId, TurnOutcome outcome, SceneView presented, IReadOnlyList<StoryThread>? looseEndsOverride, CancellationToken ct,
        IProgress<string>? progress = null)
    {
        var quality = llmOptions.Value;
        var setting = await EnsureSettingAsync(saveId, ct).ConfigureAwait(false);
        var cast = await CastAsync(saveId, setting, ct).ConfigureAwait(false);
        await EnsureBibleAsync(saveId, setting, cast, ct, progress).ConfigureAwait(false);

        var flags = await state.GetFlagsAsync(saveId, ct).ConfigureAwait(false);
        var known = await ListKnownAsync(saveId, ct).ConfigureAwait(false);
        var names = await NamesAsync(saveId, cast, ct).ConfigureAwait(false);
        var place = known.FirstOrDefault(p => p.Id == outcome.PlaceId);
        var facts = await story.GetFactsAsync(saveId, ct).ConfigureAwait(false);

        var ends = castContent.Temper.SelectMany(a => a.Ends).ToDictionary(e => e.Id, e => e.Writing, StringComparer.Ordinal);
        var duty = outcome.Duty;
        var stages = new Dictionary<string, RelationshipStage>(StringComparer.Ordinal);
        var present = new List<PacketPerson>();
        foreach (var li in cast.Where(li => outcome.With.Contains(li.Ref)))
        {
            var relationship = await story.GetRelationshipAsync(saveId, li.Id, ct).ConfigureAwait(false);
            stages[li.Id.ToString()] = relationship.Stage;
            present.Add(new PacketPerson(
                li.Id.ToString(),
                li.Name,
                [.. li.Member.Temper.Values.Select(end => ends.GetValueOrDefault(end, ""))],
                relationship.Stage,
                EncounterEvaluator.Holds(flags, $"{li.Key}.want_revealed") ? castContent.Want(li.Member.WantId).Label : null,
                quality.Voices ? await VoiceOfAsync(saveId, setting, cast, li, facts, ct, progress).ConfigureAwait(false) : null,
                RoutineForWriter(saveId, setting, li, flags, known),
                (outcome.Arrives ?? []).Contains(li.Ref) ? PersonAway.Arriving : null));
        }

        var presentIds = present.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var ceiling = (await saves.ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(s => s.Id == saveId)?.Ceiling ?? Ceiling.PG13;
        var pack = await studio.GetPackAsync(ct).ConfigureAwait(false);

        // Memory (plan §8): the last scene shared with whoever is here, the memories most like this
        // encounter, and last week in a sentence.
        var day = outcome.VisitedAt.Day;
        progress?.Report("Remembering earlier days…");
        var remembered = await memoryStore.ListAsync(saveId, ct).ConfigureAwait(false);
        var lastShared = MemoryRetrieval.LastShared(remembered, presentIds);
        var retrieved = MemoryRetrieval.Retrieve(
            remembered, await EmbedAsync(outcome.Text, ct).ConfigureAwait(false), presentIds, day, llmOptions.Value.RetrievedMemories, lastShared?.Id);
        var lastWeek = MemoryRetrieval.LastWeek(remembered, day);

        List<string> memoryLines = [];
        if (lastShared is not null)
        {
            memoryLines.Add($"Last time together, day {lastShared.Day}: {lastShared.Summary}");
        }

        memoryLines.AddRange(retrieved.Select(m => $"Day {m.Day}: {m.Summary}"));
        if (lastWeek is not null)
        {
            memoryLines.Add($"The week up to day {lastWeek.Day}: {lastWeek.Summary}");
        }

        // What the person drawn here can be wearing; texting draws nobody.
        var dressed = presented.CharacterId is { } drawnId && outcome.EncounterId != JsonEncounterCatalog.PhoneId
            ? cast.FirstOrDefault(li => li.Id == drawnId)
            : null;
        var outfit = dressed is null
            ? null
            : await OutfitOptionsAsync(saveId, setting, cast, dressed, outcome.VisitedAt, outcome.PlaceId, outcome.EncounterId, ct).ConfigureAwait(false);

        // Anyone else who can be drawn here wears what suits them; only the person the scene is about is asked to pick.
        var others = new Dictionary<Guid, Outfit>();
        var otherOutfits = new List<PacketOutfit>();
        foreach (var other in dressed is null ? [] : cast.Where(li => outcome.With.Contains(li.Ref) && li.Id != dressed.Id))
        {
            var offered = await OutfitOptionsAsync(saveId, setting, cast, other, outcome.VisitedAt, outcome.PlaceId, outcome.EncounterId, ct).ConfigureAwait(false);
            others[other.Id] = offered.Wearing;
            otherOutfits.Add(offered with { Settled = true });
        }

        var packet = new ScenePacket(
            setting.DisplayName,
            setting.Tone,
            outcome.VisitedAt,
            outcome.PlaceId,
            place?.Name ?? outcome.PlaceId,
            names.Player,
            present,
            [.. facts.Where(f => f.Knowers.Contains(FactLedger.Player))],
            [.. facts.Where(f => !f.Knowers.Contains(FactLedger.Player) && f.Knowers.Any(presentIds.Contains))],
            (outcome.EncounterId switch
            {
                JsonEncounterCatalog.QuietCompanyId =>
                    $"{presented.Name} happens to be at {place?.Name ?? outcome.PlaceId}. Show a short, ordinary moment: what they are doing, how they react on noticing the player, maybe a line of dialogue. Nothing important happens.",
                JsonEncounterCatalog.PromisedMeetingId =>
                    $"{presented.Name} is at {place?.Name ?? outcome.PlaceId} because the two of them agreed to meet here now. Show them arriving or already waiting, glad or relieved the player came, and pick up where they left off.",
                JsonEncounterCatalog.InitiativeId =>
                    $"{presented.Name} has come to {place?.Name ?? outcome.PlaceId} looking for the player, of their own accord, after not seeing them for a while. They take the lead in a way that fits their temper and where things stand between them: say why they came, and ask or suggest something the player can answer. Do not decide the player's answer.",
                JsonEncounterCatalog.QuietAloneId =>
                    // Strangers in passing are allowed in so: with a happening, the judge read three volunteers at a campfire as
                    // breaking "without inventing anyone", which only ever meant nobody the player could get to know.
                    $"Nobody the player knows is at {place?.Name ?? outcome.PlaceId}. Show the place at this time of day and in this weather, what is going on around and what there is to do here. Strangers may be around, busy with their own things, as background: give none of them a name, and make none of them someone the player could get to know.",
                JsonEncounterCatalog.ChanceMeetingId =>
                    $"{presented.Name} is at {place?.Name ?? outcome.PlaceId}, going about their own day, and the two of them have never met. Something small and natural here brings them into conversation for the first time: {presented.Name} speaks first and says who they are. End on something the player can answer.",
                JsonEncounterCatalog.PhoneId =>
                    // Shown as bubbles on a phone: anything outside the quotes is not shown at all.
                    $"{presented.Name} is not with the player; the two of them are texting, while the player is at {place?.Name ?? outcome.PlaceId}. " +
                    $"Write only {presented.Name}'s text messages and nothing else: two to four short messages, each in quotes on its own line, with a blank line between them, " +
                    "saying what prompted them and what they want to say. No narration, no description of any place, no phone buzzing. End on something the player can answer.",
                _ => outcome.Text,
            }),
            ceiling,
            [.. pack.Expressions.Keys],
            [.. known.Select(p => p.Name)],
            memoryLines,
            WeatherOn(saveId, setting, day).Writing,
            // Alone, the choices are things to do here (user feedback: not a fixed list of activities).
            OffersChoices: (outcome.Choices ?? []).Count == 0,
            PlayerGender: await saves.GetPlayerGenderAsync(saveId, ct).ConfigureAwait(false),
            Language: await saves.GetNarrationLanguageAsync(saveId, ct).ConfigureAwait(false),
            PlayerLife: await PlayerLifeLinesAsync(saveId, setting, known, names.Player, ct).ConfigureAwait(false),
            Duty: duty,
            LooseEnds: quality.Threads ? await LooseEndsAsync(saveId, cast, presentIds, day, looseEndsOverride, ct).ConfigureAwait(false) : null,
            Happening: quality.Happenings && place is not null && outcome.EncounterId is JsonEncounterCatalog.QuietAloneId
                or JsonEncounterCatalog.QuietCompanyId or JsonEncounterCatalog.ChanceMeetingId or JsonEncounterCatalog.PromisedMeetingId or JsonEncounterCatalog.InitiativeId
                ? happenings.Pick(saveId.ToString(), place.TypeId, WeatherOn(saveId, setting, day).Id, outcome.VisitedAt)
                : null,
            VariedChoices: quality.VariedChoices,
            Outfit: outfit,
            OtherOutfits: otherOutfits,
            Drawn: dressed is null ? null : [dressed.Id.ToString(), .. others.Keys.Select(id => id.ToString())]);

        var world = new SceneWorld(
            facts,
            cast.ToDictionary(li => li.Id.ToString(), li => ScheduleFor(saveId, setting, li, flags), StringComparer.Ordinal),
            await story.GetPromisesAsync(saveId, openOnly: true, ct).ConfigureAwait(false),
            stages,
            Summoned: presentIds);

        return new BuiltScene(cast, known, packet, world, memoryLines, others);
    }

    /// <summary>
    /// How <paramref name="li"/> talks, writing the voices of everyone in the cast who has none yet, in one call, the first
    /// time one is needed. Null when no voice could be written.
    /// </summary>
    private async Task<string?> VoiceOfAsync(
        SaveId saveId, SettingDefinition setting, IReadOnlyList<LoveInterest> cast, LoveInterest li, IReadOnlyList<KnownFact> facts, CancellationToken ct,
        IProgress<string>? progress = null)
    {
        if (await story.GetVoiceAsync(li.Id, ct).ConfigureAwait(false) is { } voice)
        {
            return voice;
        }

        progress?.Report("Finding how everyone talks…");

        var ends = castContent.Temper.SelectMany(a => a.Ends).ToDictionary(e => e.Id, e => e.Writing, StringComparer.Ordinal);
        var people = new List<VoicePerson>();
        foreach (var member in cast)
        {
            if (await story.GetVoiceAsync(member.Id, ct).ConfigureAwait(false) is not null)
            {
                continue;
            }

            var id = member.Id.ToString();
            people.Add(new VoicePerson(
                id,
                member.Name,
                facts.FirstOrDefault(f => f.Fact.Subject == id && f.Fact.Predicate == "works-as")?.Fact.Object ?? "something they rarely talk about",
                [.. member.Member.Temper.Values.Select(end => ends.GetValueOrDefault(end, ""))],
                [.. facts.Where(f => f.Fact.Subject == id && f.Fact.Predicate == "likes" && f.Fact.Level is FactLevel.Core).Select(f => f.Fact.Object)]));
        }

        foreach (var (id, written) in await voiceWriter.WriteAsync(setting.DisplayName, setting.Tone, people, ct).ConfigureAwait(false))
        {
            await story.SetVoiceAsync(Guid.Parse(id), written, ct).ConfigureAwait(false);
        }

        return await story.GetVoiceAsync(li.Id, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The loose ends a scene with <paramref name="presentIds"/> picks up: the save's open ones, read once from its recent
    /// scene log the first time, or the ones given for a replay.
    /// </summary>
    private async Task<IReadOnlyList<PacketThread>> LooseEndsAsync(
        SaveId saveId, IReadOnlyList<LoveInterest> cast, IReadOnlyCollection<string> presentIds, int day, IReadOnlyList<StoryThread>? given, CancellationToken ct)
    {
        if (given is null && await threads.MarkSeededAsync(saveId, ct).ConfigureAwait(false))
        {
            foreach (var seeded in await ReadLooseEndsAsync(saveId, cast, beforeSceneId: null, ct).ConfigureAwait(false))
            {
                await threads.AddAsync(saveId, string.IsNullOrEmpty(seeded.About) ? null : seeded.About, seeded.Text, day, ct).ConfigureAwait(false);
            }
        }

        var open = given ?? await threads.ListAsync(saveId, ct: ct).ConfigureAwait(false);
        return [.. StoryThreads.ForScene(open, presentIds, day).Select(t => new PacketThread(t.Id, t.Text, t.OpenedDay))];
    }

    /// <summary>The loose ends the last few written scenes (before <paramref name="beforeSceneId"/>, when given) left open, read by the model.</summary>
    private async Task<IReadOnlyList<SeededThread>> ReadLooseEndsAsync(SaveId saveId, IReadOnlyList<LoveInterest> cast, long? beforeSceneId, CancellationToken ct)
    {
        const int Scenes = 6;
        const int PerScene = 2500;

        var recent = (await sceneLog.ListAsync(saveId, ct).ConfigureAwait(false))
            .Where(s => s.Written && (beforeSceneId is null || s.Id < beforeSceneId))
            .TakeLast(Scenes)
            .Select(s => $"Day {s.Clock.Day}, {s.Clock.Slot}, at {s.PlaceId}:\n{s.Text}" + string.Concat(s.Exchanges.Select(e => $"\n\nYou: {e.Reply}\n\n{e.Reaction}")))
            .Select(text => text.Length > PerScene ? text[..PerScene] : text)
            .ToList();

        return await threadWriter.FromHistoryAsync(recent, [.. cast.Select(li => (li.Id.ToString(), li.Name))], ct).ConfigureAwait(false);
    }

    /// <summary>Keeps what an answer opened and settles what it said it settled.</summary>
    private async Task KeepThreadsAsync(SaveId saveId, Guid? about, IReadOnlyList<string>? opened, IReadOnlyList<long>? settled, int day, CancellationToken ct)
    {
        if (settled is { Count: > 0 })
        {
            await threads.CloseAsync(saveId, settled, day, ct).ConfigureAwait(false);
        }

        foreach (var text in opened ?? [])
        {
            await threads.AddAsync(saveId, about?.ToString(), text, day, ct).ConfigureAwait(false);
        }
    }

    /// <summary>A past scene written again with the current settings, stored nowhere: for comparing ways of writing.</summary>
    /// <param name="Packet">The packet as the writer saw it.</param>
    public sealed record ReplayedScene(string Packet, WrittenScene Written, double Seconds);

    /// <summary>A past scene's first reply answered again with the current settings, stored nowhere.</summary>
    public sealed record ReplayedReaction(string SceneText, string Reply, string Packet, WrittenReaction Written, double Seconds);

    /// <summary>Makes sure a save has what replays need written once and kept: its story bible, and voices when they are on.</summary>
    public async Task PrepareReplayAsync(SaveId saveId, CancellationToken ct = default)
    {
        var setting = await EnsureSettingAsync(saveId, ct).ConfigureAwait(false);
        var cast = await CastAsync(saveId, setting, ct).ConfigureAwait(false);
        await EnsureBibleAsync(saveId, setting, cast, ct).ConfigureAwait(false);

        if (cast.Count > 0)
        {
            await VoiceOfAsync(saveId, setting, cast, cast[0], await story.GetFactsAsync(saveId, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        }
    }

    /// <summary>The loose ends open just before a past scene, read from the scenes before it, numbered from 1; for replays.</summary>
    public async Task<IReadOnlyList<StoryThread>> LooseEndsBeforeAsync(SaveId saveId, long sceneId, CancellationToken ct = default)
    {
        var setting = await EnsureSettingAsync(saveId, ct).ConfigureAwait(false);
        var cast = await CastAsync(saveId, setting, ct).ConfigureAwait(false);
        var scene = (await sceneLog.ListAsync(saveId, ct).ConfigureAwait(false)).FirstOrDefault(s => s.Id == sceneId)
            ?? throw new InvalidOperationException($"Save '{saveId}' has no scene {sceneId}.");

        return
        [
            .. (await ReadLooseEndsAsync(saveId, cast, sceneId, ct).ConfigureAwait(false))
                .Select((t, i) => new StoryThread(i + 1, string.IsNullOrEmpty(t.About) ? null : t.About, t.Text, scene.Clock.Day, null)),
        ];
    }

    /// <summary>Writes a past scene again with the current settings and returns it, storing nothing.</summary>
    public async Task<ReplayedScene> ReplaySceneAsync(SaveId saveId, long sceneId, IReadOnlyList<StoryThread>? looseEnds, CancellationToken ct = default)
    {
        var stored = (await sceneLog.ListAsync(saveId, ct).ConfigureAwait(false)).FirstOrDefault(s => s.Id == sceneId)
            ?? throw new InvalidOperationException($"Save '{saveId}' has no scene {sceneId}.");
        var outcome = TurnOutcomeJson.Deserialize(stored.OutcomeJson);
        var presented = await PresentAsync(saveId, outcome, ct).ConfigureAwait(false);
        var built = await BuildScenePacketAsync(saveId, outcome, presented, looseEnds ?? [], ct).ConfigureAwait(false);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var written = await sceneWriter.WriteAsync(built.Packet, built.World, outcome.Text, [.. built.Known.Select(p => p.Name)], built.Packet.OffersChoices, ct)
            .ConfigureAwait(false);
        return new ReplayedScene(ScenePacketBuilder.Render(built.Packet), written, watch.Elapsed.TotalSeconds);
    }

    /// <summary>Answers a past scene's first reply again with the current settings and returns it, storing nothing; null for a scene nobody replied to.</summary>
    public async Task<ReplayedReaction?> ReplayReactionAsync(SaveId saveId, long sceneId, IReadOnlyList<StoryThread>? looseEnds, CancellationToken ct = default)
    {
        var stored = (await sceneLog.ListAsync(saveId, ct).ConfigureAwait(false)).FirstOrDefault(s => s.Id == sceneId)
            ?? throw new InvalidOperationException($"Save '{saveId}' has no scene {sceneId}.");
        if (stored.Exchanges.Count == 0 || !stored.Written)
        {
            return null;
        }

        var outcome = TurnOutcomeJson.Deserialize(stored.OutcomeJson);
        var pending = new PendingScene(stored.Clock, stored.PlaceId, stored.EncounterId, outcome.With, stored.Text, []);
        var built = await BuildReactionPacketAsync(saveId, pending, outcome.Duty, looseEnds ?? [], ct).ConfigureAwait(false);
        var owner = Owner(built.Cast, pending.With);
        var reply = stored.Exchanges[0].Reply;

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var written = await reactionWriter.WriteAsync(
            built.Packet, stored.Text, reply, null, $"{owner?.Name ?? "They"} takes that in.", 1, SceneConversation.CeilingFor(stored.EncounterId), SceneConversation.WindDownFor(stored.EncounterId), ct).ConfigureAwait(false);
        return new ReplayedReaction(stored.Text, reply, ScenePacketBuilder.Render(built.Packet), written, watch.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// Answers the scene waiting for a reply (phase-3 plan: choices): a proposed choice by index, or the
    /// player's own words. The reaction is written, the reply's tags are scored for everyone present
    /// and committed with closing the scene, and a popup is returned only for a considerable reaction.
    /// </summary>
    public async Task<ReactionResult> RespondAsync(SaveId saveId, int? choiceIndex, string? freeText, CancellationToken ct = default)
    {
        var scene = await state.GetPendingSceneAsync(saveId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No scene is waiting for a reply.");

        string words;
        IReadOnlyList<string>? chosenTags;
        if (choiceIndex is { } index)
        {
            var choice = scene.Choices.ElementAtOrDefault(index)
                ?? throw new InvalidOperationException($"There is no choice {index}.");
            words = choice.Text;
            chosenTags = choice.Tags;
        }
        else
        {
            words = freeText?.Trim() ?? "";
            if (words.Length is 0 or > ReactionWriter.MaxReplyLength)
            {
                throw new InvalidOperationException($"A reply needs 1 to {ReactionWriter.MaxReplyLength} characters.");
            }

            chosenTags = null;
        }

        return await RespondCoreAsync(saveId, scene, words, chosenTags, logChoice: true, choicePopup: null, rewrite: false, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks for the last answer again, when it came back as the placeholder. The scene is put back as it
    /// stood before the reply and the same reply is answered afresh, so nothing is counted twice: a chosen
    /// reply keeps the score its tags already earned, and free words are scored by what the new answer reads
    /// in them. Returns null when there is no answered scene to rewrite.
    /// </summary>
    public async Task<ReactionResult?> RewriteReactionAsync(SaveId saveId, CancellationToken ct = default)
    {
        var open = await sceneLog.GetOpenAsync(saveId, ct).ConfigureAwait(false);
        if (open is null || open.Exchanges.Count == 0)
        {
            return null;
        }

        var last = open.Exchanges[^1];
        var outcome = TurnOutcomeJson.Deserialize(open.OutcomeJson);

        // The scene as the writer read it before this reply: its words and every exchange before the last.
        var text = open.Exchanges
            .Take(open.Exchanges.Count - 1)
            .Aggregate(open.Text, (sofar, e) => SceneConversation.Transcript(sofar, e.Reply, e.Reaction ?? ""));

        // Put back the scene that was waiting, so the reply is answered as it was the first time.
        var scene = new PendingScene(open.Clock, open.PlaceId, open.EncounterId, outcome.With, text, [], open.Exchanges.Count - 1);
        await state.SavePendingSceneAsync(saveId, scene, ct).ConfigureAwait(false);

        return await RespondCoreAsync(
            saveId, scene, last.Reply, last.Proposed ? [] : null, logChoice: false, choicePopup: last.Popup, rewrite: true, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the answer to <paramref name="words"/>, scores its tags for everyone present, and either closes
    /// the waiting scene or keeps the conversation going with what the player can say next.
    /// </summary>
    /// <param name="chosenTags">A proposed choice's tags, or empty for a choice already scored; null for free text.</param>
    /// <param name="logChoice">False when the choice was already logged for the recap.</param>
    /// <param name="choicePopup">A popup the choice already earned, shown instead of this reaction's.</param>
    /// <param name="rewrite">True when the placeholder answer is being asked for again: it takes the place of the exchange it failed.</param>
    private async Task<ReactionResult> RespondCoreAsync(
        SaveId saveId,
        PendingScene scene,
        string words,
        IReadOnlyList<string>? chosenTags,
        bool logChoice,
        string? choicePopup,
        bool rewrite,
        CancellationToken ct)
    {
        var openScene = await sceneLog.GetOpenAsync(saveId, ct).ConfigureAwait(false);
        var duty = openScene is null ? null : TurnOutcomeJson.Deserialize(openScene.OutcomeJson).Duty;
        var shown = openScene?.Figures;
        var built = await BuildReactionPacketAsync(saveId, scene, duty, looseEndsOverride: null, ct, shown).ConfigureAwait(false);
        var (setting, cast, flags, known, presentPeople, before, packet) =
            (built.Setting, built.Cast, built.Flags, built.Known, built.PresentPeople, built.Before, built.Packet);
        var pack = await studio.GetPackAsync(ct).ConfigureAwait(false);

        var owner = Owner(cast, scene.With);
        var reaction = await reactionWriter.WriteAsync(
            packet, scene.Text, words, chosenTags, $"{owner?.Name ?? "They"} takes that in.", scene.Replies + 1, SceneConversation.CeilingFor(scene.EncounterId), SceneConversation.WindDownFor(scene.EncounterId), ct).ConfigureAwait(false);

        // Without a model every answer is the placeholder, and there is nothing to try again: the warning
        // is only for a writer that was asked and failed.
        var warn = reaction.Fallback && llmOptions.Value.Enabled;

        // "{want}" in a tag means the want of the person the scene is about.
        var tags = reaction.Tags.Select(t => t.Replace(StoryContent.WantToken, owner?.Member.WantId ?? "", StringComparison.Ordinal)).ToList();

        var after = new Dictionary<string, string>(flags, StringComparer.Ordinal);
        var toSet = new Dictionary<string, string>(StringComparer.Ordinal);
        var relationships = new Dictionary<Guid, RelationshipState>();
        foreach (var li in presentPeople)
        {
            var delta = _engine.Score(li.Profile, li.Member.Temper, li.Member.WantId, tags);
            relationships[li.Id] = Advance(li, _engine.Apply(before[li.Id], delta, scene.Clock.Day), after, toSet);
        }

        // Swapped numbers are the model's to report and C#'s to record: only with the person the scene is about,
        // and only once (user feedback: a fixed "ask for their number" choice felt fake).
        string? swapped = null;
        if (reaction.ExchangedNumbers && owner is not null && !EncounterEvaluator.Holds(flags, $"{owner.Key}.contact"))
        {
            toSet[$"{owner.Key}.contact"] = "true";
            swapped = $"You have {owner.Name}'s number now.";
        }

        // Places the reply named, homes among them, and what people said about their weeks.
        if (!reaction.Fallback)
        {
            foreach (var (key, value) in await AddPlacesAsync(saveId, reaction.Places, presentPeople, scene.Clock.Day, ct).ConfigureAwait(false))
            {
                toSet[key] = value;
            }

            await LearnRoutinesAsync(saveId, presentPeople, reaction.Routines, toSet, scene.Clock.Day, ct).ConfigureAwait(false);
        }

        // On their own, what the player chose to do shows who they are: its qualities build their traits.
        if (presentPeople.Count == 0)
        {
            PlayerLife.Show(toSet, flags, tags.Where(t => storyContent.Values.Desires.Any(d => d.Id == t)));
        }

        if (!reaction.Fallback && packet.LooseEnds is not null)
        {
            await KeepThreadsAsync(saveId, owner?.Id, reaction.Threads, reaction.Resolved, scene.Clock.Day, ct).ConfigureAwait(false);
        }

        await state.ResolvePendingSceneAsync(saveId, relationships, toSet, ct).ConfigureAwait(false);

        // The scene waits again, holding everything said so far, up to a limit: with what the answer proposes the
        // player could say next, or with nothing proposed, for the player's own words beside Continue.
        var transcript = SceneConversation.Transcript(scene.Text, words, reaction.Text);
        var stillOpen = scene.Replies + 1 < SceneConversation.CeilingFor(scene.EncounterId);
        IReadOnlyList<ProposedChoice> next = stillOpen && !reaction.Fallback && !reaction.Ends ? reaction.Choices ?? [] : [];
        if (stillOpen)
        {
            await state.SavePendingSceneAsync(saveId, scene with { Text = transcript, Choices = next, Replies = scene.Replies + 1 }, ct).ConfigureAwait(false);
        }

        if (logChoice)
        {
            await story.LogTurnAsync(saveId, scene.Clock, ChoiceLogKind, new ChoiceRecord(
                scene.Clock.Day,
                scene.Clock.Slot.ToString(),
                words,
                [.. presentPeople.Select(li => Effect(li.Name, before[li.Id], relationships[li.Id]))]), ct).ConfigureAwait(false);
        }

        await story.LogTurnAsync(saveId, scene.Clock, reaction.Fallback ? "reaction-fallback" : "reaction", new
        {
            Reply = words,
            Proposed = chosenTags is not null,
            Tags = tags,
            reaction.Text,
            reaction.Expression,
            reaction.Outfit,
            reaction.Attempts,
            reaction.Rejections,
        }, ct).ConfigureAwait(false);

        var popup = choicePopup ?? (owner is not null && relationships.TryGetValue(owner.Id, out var changed)
            ? ReactionPopup.For(owner.Name, before[owner.Id], changed)
            : null);

        var expression = owner is null
            ? null
            : ScenePresentation.Expression(reaction.Expression, owner.Member.RestingExpression(castContent), [.. pack.Expressions.Keys]);

        // A meeting agreed in the reaction is held as a promise, if the story can hold it. Known places are read again:
        // the reply may have just named the place it agrees on.
        string? agreed = null;
        var goingTogether = false;
        var knownNow = await ListKnownAsync(saveId, ct).ConfigureAwait(false);
        if (owner is not null
            && MeetingAgreement.ToPromise(reaction.Meet, owner.Id.ToString(), scene.Clock, setting.Days, knownNow.Select(p => (p.Id, p.Name))) is { } promise)
        {
            var heldPromises = await story.GetPromisesAsync(saveId, openOnly: true, ct).ConfigureAwait(false);
            var together = MeetingAgreement.IsNow(promise);

            // Going somewhere together now is held on top of a later meeting; otherwise one open meeting a person.
            if (together ? heldPromises.All(p => p.Id != promise.Id) : heldPromises.All(p => p.CharacterId != promise.CharacterId))
            {
                await story.AddPromiseAsync(saveId, promise, ct).ConfigureAwait(false);
                var placeName = knownNow.First(p => p.Id == promise.PlaceId).Name;
                goingTogether = together;
                agreed = together
                    ? $"{owner.Name} is going to {placeName} with you. Go there next."
                    : $"You agreed to meet {owner.Name} at {placeName} on day {promise.DueDay}, {promise.DueSlot.ToString()!.ToLowerInvariant()}.";
            }
        }

        agreed = swapped is null ? agreed : agreed is null ? swapped : $"{swapped} {agreed}";

        // Who is still here once the answer is shown, and whether the person it is about changed what they wear.
        var redressed = false;
        var wearing = new Dictionary<Guid, Outfit>();
        if (owner is not null && reaction.Outfit is { } put && packet.Outfit is { } had && put != had.Wearing)
        {
            wearing[owner.Id] = put;
        }

        // Texting has no stage; a scene whose stage was never kept stands everyone it is with.
        IReadOnlyList<SceneFigure> figures = scene.EncounterId == JsonEncounterCatalog.PhoneId
            ? []
            : await StageAfterAsync(cast, scene.With, shown ?? [], reaction.Present, owner, reaction.Expression, shown is null, wearing, ct).ConfigureAwait(false);

        if (openScene is not null)
        {
            await sceneLog.SetFiguresAsync(openScene.Id, figures, ct).ConfigureAwait(false);

            var exchange = new SceneExchange(words, reaction.Text, popup, agreed, Proposed: chosenTags is not null, Fallback: warn);
            if (rewrite)
            {
                await sceneLog.ReplaceLastExchangeAsync(openScene.Id, exchange, ct).ConfigureAwait(false);
            }
            else
            {
                await sceneLog.AddExchangeAsync(openScene.Id, exchange, ct).ConfigureAwait(false);
            }

            // Something put on or changed into in the conversation, like a jacket the player offered, is drawn from now on
            // and kept into the next slot together.
            if (reaction.Outfit is { } now && packet.Outfit is { } was && now != was.Wearing)
            {
                await sceneLog.SetOutfitAsync(openScene.Id, now, ct).ConfigureAwait(false);
                redressed = true;
            }
        }

        return new ReactionResult(
            new SceneView(reaction.Text, owner?.Id, owner?.Name, owner?.Member.Aesthetic, expression) { Fallback = warn, Figures = figures },
            popup,
            agreed,
            transcript,
            next,
            stillOpen,
            redressed,
            goingTogether,
            warn);
    }

    private sealed record BuiltReaction(
        SettingDefinition Setting,
        IReadOnlyList<LoveInterest> Cast,
        IReadOnlyDictionary<string, string> Flags,
        IReadOnlyList<PlaceRecord> Known,
        List<LoveInterest> PresentPeople,
        Dictionary<Guid, RelationshipState> Before,
        ScenePacket Packet);

    /// <summary>Everything the writer is given to answer a reply in a waiting scene, with the quality material that is switched on.</summary>
    /// <param name="looseEndsOverride">Loose ends to use instead of the save's own, for replaying a past scene.</param>
    /// <param name="shown">Who stands on the stage now; anyone the scene is with but not among them is not here. Null when unknown.</param>
    private async Task<BuiltReaction> BuildReactionPacketAsync(
        SaveId saveId, PendingScene scene, string? duty, IReadOnlyList<StoryThread>? looseEndsOverride, CancellationToken ct,
        IReadOnlyList<SceneFigure>? shown = null)
    {
        var byText = scene.EncounterId == JsonEncounterCatalog.PhoneId;
        var quality = llmOptions.Value;
        var setting = await EnsureSettingAsync(saveId, ct).ConfigureAwait(false);
        var cast = await CastAsync(saveId, setting, ct).ConfigureAwait(false);
        var flags = await state.GetFlagsAsync(saveId, ct).ConfigureAwait(false);
        var known = await ListKnownAsync(saveId, ct).ConfigureAwait(false);
        var names = await NamesAsync(saveId, cast, ct).ConfigureAwait(false);
        var pack = await studio.GetPackAsync(ct).ConfigureAwait(false);
        var facts = await story.GetFactsAsync(saveId, ct).ConfigureAwait(false);
        var ceiling = (await saves.ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(s => s.Id == saveId)?.Ceiling ?? Ceiling.PG13;

        var ends = castContent.Temper.SelectMany(a => a.Ends).ToDictionary(e => e.Id, e => e.Writing, StringComparer.Ordinal);
        var presentPeople = cast.Where(li => scene.With.Contains(li.Ref)).ToList();
        var present = new List<PacketPerson>();
        var before = new Dictionary<Guid, RelationshipState>();
        foreach (var li in presentPeople)
        {
            before[li.Id] = await story.GetRelationshipAsync(saveId, li.Id, ct).ConfigureAwait(false);
            present.Add(new PacketPerson(
                li.Id.ToString(),
                li.Name,
                [.. li.Member.Temper.Values.Select(end => ends.GetValueOrDefault(end, ""))],
                before[li.Id].Stage,
                EncounterEvaluator.Holds(flags, $"{li.Key}.want_revealed") ? castContent.Want(li.Member.WantId).Label : null,
                quality.Voices ? await VoiceOfAsync(saveId, setting, cast, li, facts, ct).ConfigureAwait(false) : null,
                RoutineForWriter(saveId, setting, li, flags, known),
                !byText && shown is not null && shown.All(f => f.CharacterId != li.Id) ? PersonAway.Left : null));
        }

        var presentIds = present.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var place = known.FirstOrDefault(p => p.Id == scene.PlaceId);

        // What the person here is wearing now, as the scene or an earlier reply left it, and what they could change into.
        PacketOutfit? outfit = null;
        var dressed = byText ? null : Owner(cast, scene.With);
        if (dressed is not null)
        {
            var offered = await OutfitOptionsAsync(saveId, setting, cast, dressed, scene.Clock, scene.PlaceId, scene.EncounterId, ct).ConfigureAwait(false);
            var wearing = (await sceneLog.ListAsync(saveId, ct).ConfigureAwait(false))
                .LastOrDefault(s => s.Clock == scene.Clock && s.PlaceId == scene.PlaceId)?.Outfit ?? offered.Wearing;
            outfit = offered with { Wearing = wearing, Codes = [.. offered.Codes.Append(wearing.Dress).Distinct(StringComparer.Ordinal)], Settled = true };
        }

        var packet = new ScenePacket(
            setting.DisplayName,
            setting.Tone,
            scene.Clock,
            scene.PlaceId,
            place?.Name ?? scene.PlaceId,
            names.Player,
            present,
            [.. facts.Where(f => f.Knowers.Contains(FactLedger.Player))],
            [.. facts.Where(f => !f.Knowers.Contains(FactLedger.Player) && f.Knowers.Any(presentIds.Contains))],
            scene.EncounterId == JsonEncounterCatalog.PhoneId && presentPeople.FirstOrDefault() is { } texting
                ? $"{texting.Name} and the player are texting; they are not together. The player has just replied by text; see below. " +
                  $"Write only {texting.Name}'s answering text messages and nothing else: one to three short messages, each in quotes on its own line, " +
                  "with a blank line between them. No narration."
                : "The player has just replied; see below.",
            ceiling,
            [.. pack.Expressions.Keys],
            KnownPlaces: [.. known.Select(p => p.Name)],
            Weather: WeatherOn(saveId, setting, scene.Clock.Day).Writing,
            PlayerGender: await saves.GetPlayerGenderAsync(saveId, ct).ConfigureAwait(false),
            Language: await saves.GetNarrationLanguageAsync(saveId, ct).ConfigureAwait(false),
            PlayerLife: await PlayerLifeLinesAsync(saveId, setting, known, names.Player, ct).ConfigureAwait(false),
            Duty: duty,
            LooseEnds: quality.Threads ? await LooseEndsAsync(saveId, cast, presentIds, scene.Clock.Day, looseEndsOverride, ct).ConfigureAwait(false) : null,
            VariedChoices: quality.VariedChoices,
            Outfit: outfit,
            OtherOutfits:
            [
                .. (shown ?? []).Where(f => f.CharacterId != dressed?.Id && f.Outfit is not null)
                    .Select(f => new PacketOutfit(f.Name, f.Outfit!, [f.Outfit!.Dress], Settled: true)),
            ],
            Drawn: dressed is null ? null : [dressed.Id.ToString(), .. presentPeople.Where(li => li.Id != dressed.Id).Select(li => li.Id.ToString())]);

        return new BuiltReaction(setting, cast, flags, known, presentPeople, before, packet);
    }

    /// <summary>An embedding for retrieval, or null when none is configured or the service does not answer.</summary>
    private async Task<float[]?> EmbedAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(llmOptions.Value.EmbeddingModel) || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return await embeddings.EmbedAsync(text, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException
                                   || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary>
    /// The story bible's facts, written once per save before its first scene (plan §7-8). Appearance
    /// becomes immutable core facts the player can see; C# picks each person's job from the setting;
    /// the model adds likes and a secret, which only that person knows until a scene reveals them.
    /// </summary>
    private async Task EnsureBibleAsync(
        SaveId saveId, SettingDefinition setting, IReadOnlyList<LoveInterest> cast, CancellationToken ct, IProgress<string>? progress = null)
    {
        var facts = await story.GetFactsAsync(saveId, ct).ConfigureAwait(false);
        if (cast.Count == 0 || facts.Any(f => f.Fact.Source is BibleWriter.Source or "appearance"))
        {
            return;
        }

        progress?.Report("Getting to know everyone…");

        var ends = castContent.Temper.SelectMany(a => a.Ends).ToDictionary(e => e.Id, e => e.Writing, StringComparer.Ordinal);
        var people = new List<BiblePerson>();
        var takenJobs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var li in cast)
        {
            var id = li.Id.ToString();

            foreach (var fact in FactLedger.AppearanceFacts(id, li.Member.Appearance))
            {
                await story.AddFactAsync(saveId, fact, storyContent.Predicate(fact.Predicate), [id, FactLedger.Player], ct: ct).ConfigureAwait(false);
            }

            // Deterministic from the save and person, stepping past jobs another cast member already has.
            var job = "something they rarely talk about";
            if (setting.Occupations.Count > 0)
            {
                var start = (int)(PlaceRecord.SeedFor(saveId, id) % setting.Occupations.Count);
                job = Enumerable.Range(0, setting.Occupations.Count)
                    .Select(step => setting.Occupations[(start + step) % setting.Occupations.Count])
                    .FirstOrDefault(candidate => !takenJobs.Contains(candidate))
                    ?? setting.Occupations[start];
                takenJobs.Add(job);
            }

            await story.AddFactAsync(
                saveId,
                new Fact(id, "works-as", job, FactLevel.Core, BibleWriter.Source, 0),
                storyContent.Predicate("works-as"),
                [id],
                ct: ct).ConfigureAwait(false);

            people.Add(new BiblePerson(id, li.Name, job, castContent.Want(li.Member.WantId).Label,
                [.. li.Member.Temper.Values.Select(end => ends.GetValueOrDefault(end, ""))]));
        }

        foreach (var fact in await bibleWriter.WriteAsync(setting.DisplayName, setting.Tone, people, ct).ConfigureAwait(false))
        {
            await story.AddFactAsync(saveId, fact, storyContent.Predicate(fact.Predicate), [fact.Subject], ct: ct).ConfigureAwait(false);
        }
    }

    /// <param name="Key">The flag prefix and invite value: <c>main_li</c> or a route id.</param>
    /// <param name="Ref">How encounters name them: <c>main_li</c> or <c>variant:{route}</c>.</param>
    private sealed record LoveInterest(Guid Id, string Key, string Ref, string Name, CastMember Member, StoryProfile Profile);

    /// <summary>
    /// Moves a stage forward, and records a reached <c>dating</c> as a flag, which encounters can
    /// require: an introduction waits for the main LI to be dating.
    /// </summary>
    private RelationshipState Advance(
        LoveInterest li,
        RelationshipState current,
        Dictionary<string, string> after,
        Dictionary<string, string> toSet)
    {
        var advanced = _engine.Advance(current, StageFacts.FromFlags(after, li.Key), li.Member.Temper);

        if (advanced.Stage >= RelationshipStage.Dating)
        {
            after[$"{li.Key}.dating"] = "true";
            toSet[$"{li.Key}.dating"] = "true";
        }

        return advanced;
    }

    /// <summary>Where each love interest stands, for the leaving rules and the ending check.</summary>
    /// <param name="updated">Relationship states changed by the turn in progress, not yet stored.</param>
    /// <summary>
    /// Someone who comes looking for the player this turn (phase-3 plan: initiative), the likeliest one
    /// whose roll comes up, or nobody. Nights stay the player's own.
    /// </summary>
    private async Task<LoveInterest?> SeekerAsync(
        SaveId saveId,
        IReadOnlyList<LoveInterest> cast,
        IReadOnlyDictionary<string, string> flags,
        ClockState clock,
        CancellationToken ct)
    {
        if (clock.Slot is TimeOfDay.Night)
        {
            return null;
        }

        LoveInterest? seeker = null;
        var best = 0.0;
        foreach (var status in await StatusesAsync(saveId, cast, flags, new Dictionary<Guid, RelationshipState>(), ct).ConfigureAwait(false))
        {
            if (Initiative.CoolingDown(flags.GetValueOrDefault($"{status.Key}.initiative_day"), clock.Day))
            {
                continue;
            }

            var invites = int.TryParse(flags.GetValueOrDefault($"{status.Key}.invites"), out var n) ? n : 0;
            var chance = Initiative.Chance(status, storyContent.TemperScale(status.Temper, m => m.Initiative), invites, clock.Day);

            if (chance > best && Initiative.Rolls(saveId.ToString(), status.Key, clock, chance))
            {
                seeker = cast.First(li => li.Key == status.Key);
                best = chance;
            }
        }

        return seeker;
    }

    private async Task<IReadOnlyList<RouteStatus>> StatusesAsync(
        SaveId saveId,
        IReadOnlyList<LoveInterest> cast,
        IReadOnlyDictionary<string, string> flags,
        IReadOnlyDictionary<Guid, RelationshipState>? updated,
        CancellationToken ct)
    {
        var lastSeen = await state.GetLastSeenAsync(saveId, ct).ConfigureAwait(false);
        var broken = (await story.GetPromisesAsync(saveId, openOnly: false, ct).ConfigureAwait(false))
            .Where(p => p.Status is PromiseStatus.Broken)
            .GroupBy(p => p.CharacterId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var statuses = new List<RouteStatus>(cast.Count);
        foreach (var li in cast)
        {
            var relationship = updated?.GetValueOrDefault(li.Id)
                ?? await story.GetRelationshipAsync(saveId, li.Id, ct).ConfigureAwait(false);

            statuses.Add(new RouteStatus(
                li.Key,
                EncounterEvaluator.Holds(flags, $"{li.Key}.met"),
                HasLeft(flags, li) ? flags[$"{li.Key}.left"] : null,
                relationship,
                li.Member.Temper,
                lastSeen.TryGetValue(li.Ref, out var day) ? day : null,
                broken.GetValueOrDefault(li.Id.ToString())));
        }

        return statuses;
    }

    private static bool HasLeft(IReadOnlyDictionary<string, string> flags, LoveInterest li) =>
        EncounterEvaluator.Holds(flags, $"{li.Key}.left");

    private IReadOnlyList<string> Departures(IReadOnlyList<LoveInterest> cast, IReadOnlyDictionary<string, string> flags) =>
        [.. cast.Where(li => HasLeft(flags, li)).Select(li => DepartureText(li, flags[$"{li.Key}.left"]))];

    private string DepartureText(LoveInterest li, string reason) =>
        Enum.TryParse<LeaveReason>(reason, out var parsed)
            ? endingContent.ReasonText(parsed).Replace("{who}", li.Name, StringComparison.Ordinal)
            : $"{li.Name} is gone.";

    /// <summary>The looks, temper and values of a person, for the recap.</summary>
    private IReadOnlyList<string> ProfileLines(LoveInterest li)
    {
        var desires = storyContent.Values.Desires.ToDictionary(d => d.Id, d => d.Label, StringComparer.Ordinal);
        var look = li.Member.Appearance;

        return
        [
            $"Look: {look.HairColor}, {look.HairStyle}, {look.EyeColor}, {li.Member.Aesthetic} style, {look.Age}",
            $"Temper: {string.Join(", ", li.Member.Temper.Values)}",
            $"Wanted: to {castContent.Want(li.Member.WantId).Label}",
            $"Looked for: {string.Join("; ", li.Profile.Desires.Select(d => desires.GetValueOrDefault(d.Id, d.Id)))}",
        ];
    }

    /// <summary>The setting's encounters minus any with someone who has left: a closed route stays closed.</summary>
    private IReadOnlyList<EncounterDefinition> Available(
        SaveId saveId,
        SettingDefinition setting,
        IReadOnlyList<LoveInterest> cast,
        IReadOnlyDictionary<string, string> flags)
    {
        var gone = cast.Where(li => HasLeft(flags, li)).Select(li => li.Ref).ToHashSet(StringComparer.Ordinal);
        var all = Available(saveId, setting);

        return gone.Count == 0 ? all : [.. all.Where(e => !(e.With ?? []).Any(gone.Contains))];
    }

    /// <summary>
    /// The setting's encounters as this save reads them: any whose prose its planning rewrote for the
    /// places it got carries the rewrite instead of the authored text.
    /// </summary>
    private IReadOnlyList<EncounterDefinition> Available(SaveId saveId, SettingDefinition setting)
    {
        var all = encounters.For(setting);
        var rewritten = _encounterTexts.GetValueOrDefault(saveId.ToString());

        return rewritten is not { Count: > 0 }
            ? all
            : [.. all.Select(e => rewritten.TryGetValue(e.Id, out var text) ? e with { Text = text } : e)];
    }

    /// <summary>
    /// The save's love interests with their cast records and story profiles. Variants are given a
    /// route by temper and a placeholder name the first time the cast is played, and profiles are
    /// built and stored the first time they are needed. Empty for a save without a stored cast.
    /// </summary>
    private async Task<IReadOnlyList<LoveInterest>> CastAsync(SaveId saveId, SettingDefinition setting, CancellationToken ct)
    {
        var main = await characters.GetMainAsync(saveId, ct).ConfigureAwait(false);
        var cast = main is null ? null : await characters.GetCastAsync(main.Id, ct).ConfigureAwait(false);
        if (main is null || cast is null)
        {
            return [];
        }

        var identities = await characters.GetCastIdentitiesAsync(main.Id, ct).ConfigureAwait(false);
        if (identities.Count != cast.Count)
        {
            throw new InvalidOperationException($"The cast of save '{saveId}' has {cast.Count} members but {identities.Count} identities.");
        }

        if (identities.Skip(1).Any(i => i.Route is null || i.Name is null))
        {
            var variants = cast.Skip(1).ToList();
            var assigned = RouteAssigner.Assign(variants, routes);
            var taken = identities.Select(i => i.Name).OfType<string>().ToList();
            var picked = await NamesForAsync(saveId, setting, main.Appearance.Subject, variants.Count, taken, main.AnchorSeed ?? 0, ct).ConfigureAwait(false);

            for (var i = 0; i < variants.Count; i++)
            {
                await characters.SetIdentityAsync(identities[i + 1].Id, picked[i], assigned[i], ct).ConfigureAwait(false);
            }

            identities = await characters.GetCastIdentitiesAsync(main.Id, ct).ConfigureAwait(false);
        }

        IReadOnlyList<StoryProfile>? generated = null;
        var interests = new List<LoveInterest>(cast.Count);
        for (var i = 0; i < cast.Count; i++)
        {
            var profile = await story.GetProfileAsync(identities[i].Id, ct).ConfigureAwait(false);
            if (profile is null)
            {
                generated ??= StoryProfileGenerator.For(
                    cast,
                    storyContent,
                    [.. setting.Places.Select(p => p.Type).Distinct(StringComparer.Ordinal)],
                    main.AnchorSeed ?? 0);
                profile = await story.SetProfileAsync(identities[i].Id, generated[i], ct).ConfigureAwait(false);
            }

            var key = i == 0 ? JsonEncounterCatalog.MainLiRef : identities[i].Route!;
            interests.Add(new LoveInterest(identities[i].Id, key, RefFor(key), identities[i].Name ?? "them", cast[i], profile));
        }

        return interests;
    }

    /// <summary>
    /// Names for the cast: written for this story and in its language, and topped up from the shipped
    /// pool when the model gave back fewer than there are people to name, or none at all.
    /// </summary>
    private async Task<IReadOnlyList<string>> NamesForAsync(
        SaveId saveId,
        SettingDefinition setting,
        string subject,
        int count,
        IReadOnlyList<string> taken,
        long seed,
        CancellationToken ct)
    {
        var language = await saves.GetNarrationLanguageAsync(saveId, ct).ConfigureAwait(false);
        var written = await nameWriter.WriteAsync(subject, count, taken, setting.DisplayName, setting.Tone, language, ct).ConfigureAwait(false);

        return written.Count >= count
            ? [.. written.Take(count)]
            : [.. written, .. routes.PickNames(subject, count - written.Count, [.. taken, .. written], seed)];
    }

    /// <summary>
    /// A love interest's week, anchored where the story put them: the main LI at their home place in
    /// the opening's slot, the routine variant at the routine place in the mornings, the others where
    /// they were met in the evenings.
    /// </summary>
    private static CharacterSchedule ScheduleFor(SaveId saveId, SettingDefinition setting, LoveInterest li, IReadOnlyDictionary<string, string> flags)
    {
        var (place, slot) = Anchor(setting, li, flags);
        var id = li.Id.ToString();
        return ScheduleGenerator.For(id, setting, place, slot, ScheduleGenerator.SeedFor(saveId, id), flags.GetValueOrDefault($"{li.Key}.home"));
    }

    /// <summary>
    /// Where and when a love interest's week is anchored: the main LI at their home place in the opening's slot, the others
    /// where they were met, at the time of day they were met (user feedback: a morning meeting should mean mornings there).
    /// Saves from before the time was kept fall back to mornings for the routine variant and evenings for the others.
    /// </summary>
    private static (string? Place, TimeOfDay? Slot) Anchor(SettingDefinition setting, LoveInterest li, IReadOnlyDictionary<string, string> flags)
    {
        if (li.Key == JsonEncounterCatalog.MainLiRef)
        {
            return (flags.GetValueOrDefault("main_li.home_place"), setting.Openings.FirstOrDefault(o => o.Id == flags.GetValueOrDefault("opening"))?.Time);
        }

        TimeOfDay? met = Enum.TryParse<TimeOfDay>(flags.GetValueOrDefault($"{li.Key}.slot"), out var slot) ? slot : null;
        return li.Key == "routine"
            ? (setting.RoutinePlace, met ?? TimeOfDay.Morning)
            : (flags.GetValueOrDefault($"{li.Key}.place"), met ?? TimeOfDay.Evening);
    }

    /// <summary>
    /// What the player knows of someone's week: where and when they first met (once met), their home at night (once named),
    /// and what they learned by being told or by finding them there twice.
    /// </summary>
    private static IReadOnlyList<RoutineEntry> KnownRoutine(SettingDefinition setting, LoveInterest li, CharacterSchedule schedule, IReadOnlyDictionary<string, string> flags)
    {
        var given = new List<RoutineEntry>();
        if (EncounterEvaluator.Holds(flags, $"{li.Key}.met") && Anchor(setting, li, flags) is { Place: { } place, Slot: { } slot })
        {
            given.Add(new RoutineEntry(RoutineKnowledge.Weekdays, slot, place));
        }

        if (flags.GetValueOrDefault($"{li.Key}.home") is { } home)
        {
            given.Add(new RoutineEntry(RoutineKnowledge.Daily, TimeOfDay.Night, home));
        }

        return RoutineKnowledge.Known(schedule, li.Key, flags, given);
    }

    /// <summary>What the player knows of each met person's week, for the map.</summary>
    private static IReadOnlyList<string> Routines(
        SaveId saveId, SettingDefinition setting, IReadOnlyList<LoveInterest> cast, IReadOnlyDictionary<string, string> flags, IReadOnlyList<PlaceRecord> known) =>
    [
        .. cast
            .Where(li => EncounterEvaluator.Holds(flags, $"{li.Key}.met") && !HasLeft(flags, li))
            .Select(li => (li.Name, Known: KnownRoutine(setting, li, ScheduleFor(saveId, setting, li, flags), flags)))
            .Where(p => p.Known.Count > 0)
            .Select(p => $"{p.Name} is usually around: {RoutineKnowledge.Describe(p.Known, id => PlaceName(setting, known, id))}."),
    ];

    /// <summary>Someone's whole week as the writer is told it, so they can mention it; their home by name once the story has named it.</summary>
    private static string? RoutineForWriter(
        SaveId saveId, SettingDefinition setting, LoveInterest li, IReadOnlyDictionary<string, string> flags, IReadOnlyList<PlaceRecord> known)
    {
        var week = RoutineKnowledge.Describe(RoutineKnowledge.Entries(ScheduleFor(saveId, setting, li, flags)), id => PlaceName(setting, known, id));
        var home = flags.ContainsKey($"{li.Key}.home") ? "" : (week.Length > 0 ? "; " : "") + "nights at home, a place the player does not know yet";
        return week.Length + home.Length == 0 ? null : week + home;
    }

    /// <summary>
    /// Keeps the places a scene or reply named (user request: a place mentioned in conversation can be visited later).
    /// A place the save already has is made known; a new one becomes a story place. When it is the home of someone present
    /// who has none yet, it becomes their home, drawn as the setting's home type, and they spend their nights there.
    /// Returns the flags that record new homes.
    /// </summary>
    private async Task<Dictionary<string, string>> AddPlacesAsync(
        SaveId saveId, IReadOnlyList<Game.Core.Places.PlaceProposal>? proposals, IReadOnlyList<LoveInterest> present, int day, CancellationToken ct)
    {
        var homes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (proposals is not { Count: > 0 })
        {
            return homes;
        }

        var setting = await EnsureSettingAsync(saveId, ct).ConfigureAwait(false);
        var flags = await state.GetFlagsAsync(saveId, ct).ConfigureAwait(false);
        var all = (await places.ListAsync(saveId, knownOnly: false, ct).ConfigureAwait(false)).ToList();
        var types = placeTypes.All();
        bool IsHome(string typeId) => types.FirstOrDefault(t => t.Id == typeId)?.Dress == DressCode.Home;

        foreach (var proposal in proposals)
        {
            var owner = present.FirstOrDefault(li => li.Id.ToString() == proposal.Owner);
            var homeless = owner is not null && !flags.ContainsKey($"{owner.Key}.home") && !homes.ContainsKey($"{owner.Key}.home");

            var record = all.FirstOrDefault(p => PlaceProposals.SameName(p.Name, proposal.Name));
            if (record is null)
            {
                var typeId = types.FirstOrDefault(t => t.Id == proposal.Type)?.Id;
                if (homeless && (typeId is null || !IsHome(typeId)))
                {
                    typeId = HomeTypeOf(setting);
                }

                if (typeId is null)
                {
                    continue;
                }

                // Only details the type offers: a reply's places are not sent back for a wrong detail, just cleaned.
                var offered = (types.First(t => t.Id == typeId).Details ?? []).Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
                // A look that runs long is cut to fit rather than costing the place, as details are cleaned.
                var cleaned = proposal with
                {
                    Look = PlaceProposals.CleanLook(proposal.Look),
                    Type = typeId,
                    Details = [.. (proposal.Details ?? []).Where(offered.Contains).Distinct(StringComparer.Ordinal).Take(PlaceProposals.MaxDetails)],
                };

                if (PlaceProposals.Check(cleaned, placeTypes, all.Where(p => p.Known).Select(p => p.Name)).Count > 0)
                {
                    continue;
                }

                record = PlaceProposals.ToRecord(saveId, cleaned, all.Select(p => p.Id), day);
                await places.AddAsync([record], ct).ConfigureAwait(false);
                all.Add(record);
            }
            else if (!record.Known)
            {
                await places.MarkKnownAsync(saveId, record.Id, day, ct).ConfigureAwait(false);
            }

            if (homeless && IsHome(record.TypeId))
            {
                homes[$"{owner!.Key}.home"] = record.Id;
                await story.AddFactAsync(
                    saveId,
                    new Fact(owner.Id.ToString(), "lives-at", record.Name, FactLevel.Established, "scene", day),
                    storyContent.Predicate("lives-at"),
                    [owner.Id.ToString(), FactLedger.Player],
                    ct: ct).ConfigureAwait(false);
            }
        }

        return homes;
    }

    /// <summary>The place type a love interest's home is drawn as in this setting; an apartment when the setting names none that is a home.</summary>
    private string HomeTypeOf(SettingDefinition setting) =>
        setting.HomeType is { } type && placeTypes.All().Any(t => t.Id == type && t.Dress == DressCode.Home) ? type : "apartment";

    /// <summary>
    /// Learns what someone present said about their own week, when their schedule agrees: the part of the week it names
    /// becomes known, and the place with it. What does not match their week is let go.
    /// </summary>
    private async Task LearnRoutinesAsync(
        SaveId saveId, IReadOnlyList<LoveInterest> present, IReadOnlyList<ProposedRoutine>? mentioned, Dictionary<string, string> toSet, int day, CancellationToken ct)
    {
        if (mentioned is not { Count: > 0 } || present.Count == 0)
        {
            return;
        }

        var setting = await EnsureSettingAsync(saveId, ct).ConfigureAwait(false);
        var flags = new Dictionary<string, string>(await state.GetFlagsAsync(saveId, ct).ConfigureAwait(false), StringComparer.Ordinal);
        foreach (var (key, value) in toSet)
        {
            flags[key] = value;
        }

        var all = await places.ListAsync(saveId, knownOnly: false, ct).ConfigureAwait(false);
        foreach (var said in mentioned)
        {
            if (present.FirstOrDefault(li => li.Id.ToString() == said.Who) is not { } li
                || !Enum.TryParse<TimeOfDay>(said.Slot, ignoreCase: true, out var slot)
                || all.FirstOrDefault(p => PlaceProposals.SameName(p.Name, said.Place ?? "")) is not { } place)
            {
                continue;
            }

            var days = said.Days?.Trim().ToLowerInvariant() switch
            {
                RoutineKnowledge.Weekdays => RoutineKnowledge.Weekdays,
                RoutineKnowledge.Weekend => RoutineKnowledge.Weekend,
                _ => RoutineKnowledge.Daily,
            };

            var entry = RoutineKnowledge.Entries(ScheduleFor(saveId, setting, li, flags))
                .FirstOrDefault(e => e.Slot == slot && e.PlaceId == place.Id && (e.Days == days || e.Days == RoutineKnowledge.Daily || days == RoutineKnowledge.Daily));
            if (entry is null)
            {
                continue;
            }

            toSet[RoutineKnowledge.LearnedKey(li.Key, entry)] = entry.PlaceId;
            if (!place.Known)
            {
                await places.MarkKnownAsync(saveId, place.Id, day, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// A line about the player's job with the place it is at filled in. The setting names the job without
    /// naming the shop, because which shop it is now depends on how the save was planned.
    /// </summary>
    private static string JobText(string text, string place) => text.Replace("{place}", place, StringComparison.Ordinal);

    private static string RefFor(string key) =>
        key == JsonEncounterCatalog.MainLiRef ? key : JsonEncounterCatalog.VariantPrefix + key;

    private EncounterDefinition Encounter(SaveId saveId, SettingDefinition setting, string id) =>
        Available(saveId, setting).FirstOrDefault(e => e.Id == id)
        ?? throw new InvalidOperationException($"Setting '{setting.Id}' has no encounter '{id}'.");

    private sealed record Names(Guid? MainLiId, string MainLi, string Player);

    private async Task<Names> NamesAsync(SaveId saveId, IReadOnlyList<LoveInterest> cast, CancellationToken ct)
    {
        var player = await saves.GetPlayerNameAsync(saveId, ct).ConfigureAwait(false);
        var playerName = string.IsNullOrWhiteSpace(player) ? "you" : player;

        if (cast.FirstOrDefault(li => li.Key == JsonEncounterCatalog.MainLiRef) is { } lead)
        {
            return new Names(lead.Id, lead.Name, playerName);
        }

        // A save from before the cast was stored still has a main LI with a name.
        var main = await characters.GetMainAsync(saveId, ct).ConfigureAwait(false);
        var mainName = main is null ? null : await characters.GetNameAsync(main.Id, ct).ConfigureAwait(false);
        return new Names(main?.Id, string.IsNullOrWhiteSpace(mainName) ? "them" : mainName, playerName);
    }

    /// <summary>Who a scene is about: the first variant in it, otherwise the main LI if present.</summary>
    private static LoveInterest? Owner(IReadOnlyList<LoveInterest> cast, IReadOnlyList<string> with)
    {
        var reference = with.FirstOrDefault(w => w.StartsWith(JsonEncounterCatalog.VariantPrefix, StringComparison.Ordinal))
            ?? with.FirstOrDefault(w => w == JsonEncounterCatalog.MainLiRef);

        return reference is null ? null : cast.FirstOrDefault(li => li.Ref == reference);
    }

    /// <summary>Fills an encounter's text, with <c>{who}</c>, <c>{want}</c> and <c>{need}</c> taken from the person it is about.</summary>
    private string Fill(string text, Names names, LoveInterest? owner, string place, ClockState clock)
    {
        var want = owner is null ? "" : castContent.Want(owner.Member.WantId).Label;
        var need = owner is null ? "" : storyContent.Values.Needs.FirstOrDefault(n => n.Id == owner.Profile.Need)?.Label ?? "";

        return text.Replace("{main_li}", names.MainLi, StringComparison.Ordinal)
            .Replace("{player}", names.Player, StringComparison.Ordinal)
            .Replace("{who}", owner?.Name ?? names.MainLi, StringComparison.Ordinal)
            .Replace("{want}", want, StringComparison.Ordinal)
            .Replace("{need}", need, StringComparison.Ordinal)
            .Replace("{place}", place, StringComparison.Ordinal)
            .Replace("{slot}", clock.Slot.ToString().ToLowerInvariant(), StringComparison.Ordinal);
    }

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

        if (Has("main_li.contact") && !Has("main_li.first_date"))
        {
            return [$"You have {mainLi}'s number. Ask them to meet you somewhere: the meeting you agree on is your first date."];
        }

        return [];
    }

    /// <summary>
    /// Where the player might run into the people they have met, from their schedules: the next slot, today
    /// or tomorrow, that puts each of them at a place the player knows. So the days between the story's
    /// beats always have somewhere to go (user feedback: day 3 of waiting for day 5 had nothing to do).
    /// </summary>
    private static IReadOnlyList<string> Sightings(
        SaveId saveId,
        SettingDefinition setting,
        IReadOnlyList<LoveInterest> cast,
        IReadOnlyDictionary<string, string> flags,
        IReadOnlyList<PlaceRecord> known,
        ClockState clock)
    {
        var lines = new List<string>();
        foreach (var li in cast.Where(li => EncounterEvaluator.Holds(flags, $"{li.Key}.met") && !HasLeft(flags, li)))
        {
            // Only from what the player knows of their week (user request: routines are learned, not handed out).
            var schedule = ScheduleFor(saveId, setting, li, flags);
            var usual = KnownRoutine(setting, li, schedule, flags);
            var at = clock;
            for (var step = 0; step < 6 && at.Day <= clock.Day + 1; step++, at = at.Next())
            {
                if (schedule.Where(at) is { } placeId
                    && usual.Any(e => e.PlaceId == placeId && RoutineKnowledge.Fits(e, at))
                    && known.FirstOrDefault(p => p.Id == placeId) is { } place)
                {
                    lines.Add($"You might run into {li.Name} at {place.Name} {When(at, clock)}.");
                    break;
                }
            }
        }

        return lines;
    }

    private static string When(ClockState at, ClockState now)
    {
        var today = at.Day == now.Day;
        return at.Slot switch
        {
            TimeOfDay.Morning => today ? "this morning" : "tomorrow morning",
            TimeOfDay.Midday => today ? "at midday" : "tomorrow at midday",
            TimeOfDay.Afternoon => today ? "this afternoon" : "tomorrow afternoon",
            TimeOfDay.Evening => today ? "this evening" : "tomorrow evening",
            _ => today ? "tonight" : "tomorrow night",
        };
    }

    /// <summary>Whether some encounter would honour inviting <paramref name="key"/> at a place the player knows, now.</summary>
    private bool CanInvite(
        SaveId saveId,
        SettingDefinition setting,
        IReadOnlyList<LoveInterest> cast,
        IReadOnlyList<PlaceRecord> known,
        IReadOnlyDictionary<string, string> flags,
        ClockState clock,
        string key)
    {
        var requirement = $"{EncounterEvaluator.InviteKey}={key}";
        var withInvite = new Dictionary<string, string>(flags, StringComparer.Ordinal) { [EncounterEvaluator.InviteKey] = key };
        var inviteBeats = Available(saveId, setting, cast, flags).Where(e => (e.Requires ?? []).Contains(requirement)).ToList();

        return known.Any(place =>
            inviteBeats.Any(e => EncounterEvaluator.Matches(e, new TurnContext(clock, place.Id, withInvite, 0))));
    }

    /// <summary>
    /// The setting as this save plays it: its roles filled and its calendar dated by the plan laid out the
    /// first time the save is played (migration 018), or the setting as authored for a save from before
    /// planning, or one whose planning found no model.
    /// </summary>
    private async Task<SettingDefinition> EnsureSettingAsync(SaveId saveId, CancellationToken ct, IProgress<string>? progress = null)
    {
        var settingId = await saves.GetSettingIdAsync(saveId, ct).ConfigureAwait(false)
            ?? await saves.SetSettingAsync(saveId, options.Value.DefaultSettingId, ct).ConfigureAwait(false);

        var setting = settings.Get(settingId);

        if (!await plans.IsPlannedAsync(saveId, ct).ConfigureAwait(false))
        {
            setting = await PlanAsync(saveId, setting, ct, progress).ConfigureAwait(false);
        }
        else
        {
            // The places are already stored, and carry what the plan made of each role; the calendar is
            // read back, and is empty for a save laid out before there was planning.
            var stored = await places.ListAsync(saveId, knownOnly: false, ct).ConfigureAwait(false);
            var events = await plans.EventsAsync(saveId, ct).ConfigureAwait(false);
            setting = Rebuild(setting, stored, events);
        }

        if (!_encounterTexts.ContainsKey(saveId.ToString()))
        {
            _encounterTexts[saveId.ToString()] = await plans.EncounterTextsAsync(saveId, ct).ConfigureAwait(false);
        }

        await places.AddAsync(PlaceRecord.Authored(saveId, setting), ct).ConfigureAwait(false);
        return setting;
    }

    /// <summary>
    /// Lays out this save's own town, once. The plan's places are stored before anything else reads them,
    /// so a role is what the plan made of it from the first turn. Without a model, the setting is played
    /// as authored, and recorded as planned so it is not asked for again every turn.
    /// </summary>
    private async Task<SettingDefinition> PlanAsync(
        SaveId saveId, SettingDefinition setting, CancellationToken ct, IProgress<string>? progress)
    {
        // A save that already has places was laid out before there was planning, and is very likely being
        // played. Planning it now would rename the streets under someone mid-story, so it keeps the setting
        // it started with and is simply recorded as laid out.
        if ((await places.ListAsync(saveId, knownOnly: false, ct).ConfigureAwait(false)).Count > 0)
        {
            await plans.SaveAsync(saveId, planned: false, [], new Dictionary<string, string>(), ct).ConfigureAwait(false);
            return setting;
        }

        progress?.Report("Laying out the town…");

        var language = await saves.GetNarrationLanguageAsync(saveId, ct).ConfigureAwait(false);
        var beats = Plannable(setting);
        var plan = await planWriter.WriteAsync(setting, placeTypes, beats, language, ct).ConfigureAwait(false);

        var planned = plan is null ? setting : SavePlans.Apply(setting, plan);
        var records = plan is null ? PlaceRecord.Authored(saveId, planned) : SavePlans.Records(saveId, planned, plan);

        // Whoever gets here first writes the plan; a second caller reads what that one stored.
        if (!await plans.SaveAsync(saveId, plan is not null, planned.Events, plan?.EncounterTexts ?? new Dictionary<string, string>(), ct).ConfigureAwait(false))
        {
            var stored = await places.ListAsync(saveId, knownOnly: false, ct).ConfigureAwait(false);
            return Rebuild(setting, stored, await plans.EventsAsync(saveId, ct).ConfigureAwait(false));
        }

        await places.AddAsync(records, ct).ConfigureAwait(false);

        foreach (var thread in plan?.Threads ?? [])
        {
            await threads.AddAsync(saveId, null, thread, 1, ct).ConfigureAwait(false);
        }

        return planned;
    }

    /// <summary>The setting with each role as the save stored it, and the save's own calendar when it has one.</summary>
    private static SettingDefinition Rebuild(
        SettingDefinition setting, IReadOnlyList<PlaceRecord> stored, IReadOnlyList<SettingEvent> events)
    {
        var byId = stored.ToDictionary(p => p.Id, p => p, StringComparer.Ordinal);

        return setting with
        {
            Places =
            [
                .. setting.Places.Select(role => byId.TryGetValue(role.Id, out var record)
                    ? role with { Type = record.TypeId, Name = record.Name, Details = record.Details }
                    : role),
            ],
            Events = events.Count == 0 ? setting.Events : events,
        };
    }

    /// <summary>The authored beats whose prose a plan rewrites: those written by hand for one of the setting's roles.</summary>
    private IReadOnlyList<PlannableEncounter> Plannable(SettingDefinition setting) =>
    [
        .. encounters.Authored(setting.Id)
            .Where(e => !string.IsNullOrWhiteSpace(e.Text))
            .Where(e => e.Place?.Id is { } id && setting.Places.Any(p => string.Equals(p.Id, id, StringComparison.Ordinal)))
            .Select(e => new PlannableEncounter(e.Id, e.Place!.Id!, e.Text!)),
    ];

    private async Task<IReadOnlyList<PlaceRecord>> ListKnownAsync(SaveId saveId, CancellationToken ct)
    {
        var known = await places.ListAsync(saveId, knownOnly: true, ct).ConfigureAwait(false);

        return known.Count > 0
            ? known
            : throw new InvalidOperationException($"Save '{saveId}' has no known places.");
    }
}
