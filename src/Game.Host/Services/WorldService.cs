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

namespace Game.Host.Services;

/// <summary>An encounter's choice that is still open, with its text filled in.</summary>
public sealed record PendingChoice(string EncounterId, string Text, IReadOnlyList<EncounterChoice> Choices);

/// <summary>Someone the player can bring along this turn, or end the story with.</summary>
/// <param name="Key">The invite value and flag prefix: <c>main_li</c> or a route id.</param>
public sealed record Invitee(string Key, string Name);

/// <summary>What a turn's scene shows: its text, and who stands in front wearing which expression.</summary>
/// <param name="CharacterId">The person in front, or null when the scene is about no one in the cast.</param>
/// <param name="Aesthetic">Their style, which picks the sprite's outfit.</param>
/// <param name="Choices">Replies the player may give, when the scene waits for one.</param>
public sealed record SceneView(
    string Text,
    Guid? CharacterId,
    string? Name,
    string? Aesthetic,
    string? Expression,
    IReadOnlyList<ProposedChoice>? Choices = null);

/// <summary>The other people's reaction to a reply, and a popup when the reaction was considerable.</summary>
/// <param name="Agreed">A meeting the reply settled, now held as a promise.</param>
public sealed record ReactionResult(SceneView View, string? Popup, string? Agreed = null);

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
    PendingScene? PendingScene = null);

/// <summary>A save's setting, places, clock, openings, choices, turns and ending.</summary>
public sealed class WorldService(
    SaveRepository saves,
    PlaceRepository places,
    CharacterRepository characters,
    GameStateRepository state,
    StoryStateRepository story,
    ISettingCatalog settings,
    IEncounterCatalog encounters,
    StoryContent storyContent,
    CastContent castContent,
    RouteContent routes,
    EndingContent endingContent,
    WeatherContent weatherContent,
    SceneWriter sceneWriter,
    ReactionWriter reactionWriter,
    EpilogueWriter epilogueWriter,
    BibleWriter bibleWriter,
    MemoryRepository memoryStore,
    MemoryCompactor compactor,
    IEmbeddingClient embeddings,
    CharacterStudio studio,
    IOptions<LlmOptions> llmOptions,
    IOptions<StudioOptions> options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

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
            var encounter = Encounter(setting, flags[EncounterEvaluator.PendingChoiceKey]);
            var placeId = encounter.Place.Id ?? (encounter.Place.PlaceFlag is { } placeFlag ? flags.GetValueOrDefault(placeFlag) : null);
            var owner = Owner(cast, encounter.With ?? []);

            pending = new PendingChoice(
                encounter.Id,
                Fill(encounter.Text, names, owner, PlaceName(setting, known, placeId), clock),
                [.. (encounter.Choices ?? []).Select(c => c with { Text = Fill(c.Text, names, owner, "", clock) })]);
        }

        var over = clock.IsPast(setting.Days);
        var pendingScene = await state.GetPendingSceneAsync(saveId, ct).ConfigureAwait(false);

        IReadOnlyList<Invitee> invitees = over || pending is not null || pendingScene is not null || offer is not null || ending is not null
            ? []
            : [.. cast.Where(li => !HasLeft(flags, li) && CanInvite(setting, cast, known, flags, clock, li.Key)).Select(li => new Invitee(li.Key, li.Name))];

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
            invitees,
            relationships,
            people,
            offer,
            ending,
            WeatherOn(saveId, setting, clock.Day),
            pendingScene);
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
    public async Task<TurnOutcome> TakeTurnAsync(SaveId saveId, string placeId, string? invite = null, CancellationToken ct = default)
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

        var flags = new Dictionary<string, string>(await state.GetFlagsAsync(saveId, ct).ConfigureAwait(false), StringComparer.Ordinal);
        if (invite is not null)
        {
            // Transient: it shapes this turn's pick and is never stored.
            flags[EncounterEvaluator.InviteKey] = invite;
        }

        var cast = await CastAsync(saveId, play.Setting, ct).ConfigureAwait(false);

        var context = new TurnContext(
            play.Clock,
            place.Id,
            flags,
            await state.CountAloneVisitsAsync(saveId, place.Id, ct).ConfigureAwait(false));

        var outcome = TurnPlanner.Plan(play.Setting, Available(play.Setting, cast, flags), context, place.Name);

        if (invite is not null && !outcome.With.Contains(RefFor(invite)))
        {
            throw new InvalidOperationException($"Nothing at {place.Name} would bring them along.");
        }

        // A meeting the player agreed to happens when they turn up for it, whatever else was planned
        // for a quiet turn.
        var openPromises = await story.GetPromisesAsync(saveId, openOnly: true, ct).ConfigureAwait(false);
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
                if (ScheduleFor(saveId, play.Setting, li, flags).Where(play.Clock) == place.Id)
                {
                    here.Add((li, (await story.GetRelationshipAsync(saveId, li.Id, ct).ConfigureAwait(false)).Affection));
                }
            }

            if (here.OrderByDescending(h => h.Affection).Select(h => h.Person).FirstOrDefault() is { } company)
            {
                outcome = outcome with { EncounterId = JsonEncounterCatalog.QuietCompanyId, With = [company.Ref], Text = $"{company.Name} is at {place.Name} too." };
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
        foreach (var (key, value) in outcome.FlagsToSet)
        {
            after[key] = value;
        }

        // Everyone in the scene: a date at a place they like or dislike, and any stage the turn's
        // flags now allow. It all commits with the turn.
        var toSet = new Dictionary<string, string>(outcome.FlagsToSet, StringComparer.Ordinal);
        var relationships = new Dictionary<Guid, RelationshipState>();

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
        return outcome with
        {
            Text = Fill(outcome.Text, names, owner, place.Name, outcome.VisitedAt),
            Choices = [.. (outcome.Choices ?? []).Select(c => c with { Text = Fill(c.Text, names, owner, place.Name, outcome.VisitedAt) })],
        };
    }

    /// <summary>Answers the open choice. Its tags are scored for everyone in the scene.</summary>
    public async Task ChooseAsync(SaveId saveId, string choiceId, CancellationToken ct = default)
    {
        var play = await GetPlayStateAsync(saveId, ct).ConfigureAwait(false);

        var pending = play.Pending
            ?? throw new InvalidOperationException("There is no open choice.");

        var choice = pending.Choices.FirstOrDefault(c => c.Id == choiceId)
            ?? throw new InvalidOperationException($"'{choiceId}' is not an answer to the open choice.");

        var encounter = Encounter(play.Setting, pending.EncounterId);
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
        var effects = new List<ChoiceEffect>();
        foreach (var li in cast.Where(li => (encounter.With ?? []).Contains(li.Ref)))
        {
            var current = await story.GetRelationshipAsync(saveId, li.Id, ct).ConfigureAwait(false);
            var delta = _engine.Score(li.Profile, li.Member.Temper, li.Member.WantId, tags);
            relationships[li.Id] = Advance(li, _engine.Apply(current, delta, play.Clock.Day), after, sets);
            effects.Add(Effect(li.Name, current, relationships[li.Id]));
        }

        await state.ResolveChoiceAsync(saveId, pending.EncounterId, choice.Id, sets, relationships, ct).ConfigureAwait(false);
        await story.LogTurnAsync(saveId, play.Clock, ChoiceLogKind,
            new ChoiceRecord(play.Clock.Day, play.Clock.Slot.ToString(), choice.Text, effects), ct).ConfigureAwait(false);
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
                ceiling),
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
    /// Who a taken turn shows before anything is written: the person the scene is about, at their
    /// temper's resting expression, so they can be on screen while the scene is still being written.
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

        return new SceneView(outcome.Text, owner.Id, owner.Name, owner.Member.Aesthetic, expression);
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
    /// Writes the scene for a turn that has been taken (plan §8), when an LLM is configured. The packet
    /// holds only what the player and the people present know; accepted facts are stored and known by
    /// everyone present; the packet and the answer are logged so the turn can be replayed. Returns the
    /// scene text, or the encounter's authored text when there is no model or no answer passed.
    /// </summary>
    public async Task<SceneView> WriteSceneAsync(SaveId saveId, TurnOutcome outcome, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        var presented = await PresentAsync(saveId, outcome, ct).ConfigureAwait(false);

        if (!llmOptions.Value.Enabled || outcome.EncounterId is null)
        {
            return presented;
        }

        var setting = await EnsureSettingAsync(saveId, ct).ConfigureAwait(false);
        var cast = await CastAsync(saveId, setting, ct).ConfigureAwait(false);
        await EnsureBibleAsync(saveId, setting, cast, ct).ConfigureAwait(false);

        var flags = await state.GetFlagsAsync(saveId, ct).ConfigureAwait(false);
        var known = await ListKnownAsync(saveId, ct).ConfigureAwait(false);
        var names = await NamesAsync(saveId, cast, ct).ConfigureAwait(false);
        var place = known.FirstOrDefault(p => p.Id == outcome.PlaceId);

        var ends = castContent.Temper.SelectMany(a => a.Ends).ToDictionary(e => e.Id, e => e.Writing, StringComparer.Ordinal);
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
                EncounterEvaluator.Holds(flags, $"{li.Key}.want_revealed") ? castContent.Want(li.Member.WantId).Label : null));
        }

        var facts = await story.GetFactsAsync(saveId, ct).ConfigureAwait(false);
        var presentIds = present.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var ceiling = (await saves.ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(s => s.Id == saveId)?.Ceiling ?? Ceiling.PG13;
        var pack = await studio.GetPackAsync(ct).ConfigureAwait(false);

        // Memory (plan §8): the last scene shared with whoever is here, the memories most like this
        // encounter, and last week in a sentence.
        var day = outcome.VisitedAt.Day;
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
            outcome.EncounterId switch
            {
                JsonEncounterCatalog.QuietCompanyId =>
                    $"{presented.Name} happens to be at {place?.Name ?? outcome.PlaceId}. Show a short, ordinary moment: what they are doing, how they react on noticing the player, maybe a line of dialogue. Nothing important happens.",
                JsonEncounterCatalog.PromisedMeetingId =>
                    $"{presented.Name} is at {place?.Name ?? outcome.PlaceId} because the two of them agreed to meet here now. Show them arriving or already waiting, glad or relieved the player came, and pick up where they left off.",
                JsonEncounterCatalog.InitiativeId =>
                    $"{presented.Name} has come to {place?.Name ?? outcome.PlaceId} looking for the player, of their own accord, after not seeing them for a while. They take the lead in a way that fits their temper and where things stand between them: say why they came, and ask or suggest something the player can answer. Do not decide the player's answer.",
                JsonEncounterCatalog.QuietAloneId =>
                    $"Nobody the player knows is at {place?.Name ?? outcome.PlaceId}. Show the place at this time of day and in this weather, and one small thing going on around, without inventing anyone the player could get to know.",
                _ => outcome.Text,
            },
            ceiling,
            [.. pack.Expressions.Keys],
            [.. known.Select(p => p.Name)],
            memoryLines,
            WeatherOn(saveId, setting, day).Writing,
            OffersChoices: presented.CharacterId is not null && (outcome.Choices ?? []).Count == 0,
            PlayerGender: await saves.GetPlayerGenderAsync(saveId, ct).ConfigureAwait(false));

        var world = new SceneWorld(
            facts,
            cast.ToDictionary(li => li.Id.ToString(), li => ScheduleFor(saveId, setting, li, flags), StringComparer.Ordinal),
            await story.GetPromisesAsync(saveId, openOnly: true, ct).ConfigureAwait(false),
            stages,
            Summoned: presentIds);

        var written = await sceneWriter.WriteAsync(packet, world, outcome.Text, [.. known.Select(p => p.Name)], packet.OffersChoices, ct).ConfigureAwait(false);

        foreach (var fact in written.Facts)
        {
            await story.AddFactAsync(saveId, fact.Fact, storyContent.Predicate(fact.Fact.Predicate), fact.Knowers, fact.ExplainedBy, ct).ConfigureAwait(false);
        }

        // A named place the setting already has but the player did not know is revealed, not
        // duplicated; anything else becomes a story place (plan §10).
        if (written.Places.Count > 0)
        {
            var all = await places.ListAsync(saveId, knownOnly: false, ct).ConfigureAwait(false);
            var ids = all.Select(p => p.Id).ToList();

            foreach (var proposal in written.Places)
            {
                if (all.FirstOrDefault(p => string.Equals(p.Name, proposal.Name, StringComparison.OrdinalIgnoreCase)) is { } existing)
                {
                    await places.MarkKnownAsync(saveId, existing.Id, outcome.VisitedAt.Day, ct).ConfigureAwait(false);
                    continue;
                }

                var record = PlaceProposals.ToRecord(saveId, proposal, ids, outcome.VisitedAt.Day);
                ids.Add(record.Id);
                await places.AddAsync([record], ct).ConfigureAwait(false);
            }
        }

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

        await story.LogTurnAsync(saveId, outcome.VisitedAt, written.Fallback ? "scene-fallback" : "scene", new
        {
            outcome.EncounterId,
            Packet = ScenePacketBuilder.Render(packet),
            written.Text,
            written.Expression,
            written.Summary,
            written.Tags,
            Memories = memoryLines,
            Places = written.Places.Select(p => $"{p.Type}: {p.Name}"),
            written.Choices,
            written.Attempts,
            written.Rejections,
        }, ct).ConfigureAwait(false);

        // A scene with someone present waits for the player's reply before the next turn.
        IReadOnlyList<ProposedChoice> choices = !written.Fallback && packet.OffersChoices ? written.Choices ?? [] : [];
        if (choices.Count > 0)
        {
            await state.SavePendingSceneAsync(
                saveId,
                new PendingScene(outcome.VisitedAt, outcome.PlaceId, outcome.EncounterId, outcome.With, written.Text, choices),
                ct).ConfigureAwait(false);
        }

        return presented with
        {
            Text = written.Text,
            Expression = presented.Expression is null
                ? null
                : ScenePresentation.Expression(written.Expression, presented.Expression, [.. pack.Expressions.Keys]),
            Choices = choices,
        };
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
                EncounterEvaluator.Holds(flags, $"{li.Key}.want_revealed") ? castContent.Want(li.Member.WantId).Label : null));
        }

        var presentIds = present.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var place = known.FirstOrDefault(p => p.Id == scene.PlaceId);
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
            "The player has just replied; see below.",
            ceiling,
            [.. pack.Expressions.Keys],
            KnownPlaces: [.. known.Select(p => p.Name)],
            Weather: WeatherOn(saveId, setting, scene.Clock.Day).Writing,
            PlayerGender: await saves.GetPlayerGenderAsync(saveId, ct).ConfigureAwait(false));

        var owner = Owner(cast, scene.With);
        var reaction = await reactionWriter.WriteAsync(
            packet, scene.Text, words, chosenTags, $"{owner?.Name ?? "They"} takes that in.", ct).ConfigureAwait(false);

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

        await state.ResolvePendingSceneAsync(saveId, relationships, toSet, ct).ConfigureAwait(false);
        await story.LogTurnAsync(saveId, scene.Clock, ChoiceLogKind, new ChoiceRecord(
            scene.Clock.Day,
            scene.Clock.Slot.ToString(),
            words,
            [.. presentPeople.Select(li => Effect(li.Name, before[li.Id], relationships[li.Id]))]), ct).ConfigureAwait(false);

        await story.LogTurnAsync(saveId, scene.Clock, reaction.Fallback ? "reaction-fallback" : "reaction", new
        {
            Reply = words,
            Chosen = choiceIndex,
            Tags = tags,
            reaction.Text,
            reaction.Expression,
            reaction.Attempts,
            reaction.Rejections,
        }, ct).ConfigureAwait(false);

        var popup = owner is not null && relationships.TryGetValue(owner.Id, out var changed)
            ? ReactionPopup.For(owner.Name, before[owner.Id], changed)
            : null;

        var expression = owner is null
            ? null
            : ScenePresentation.Expression(reaction.Expression, owner.Member.RestingExpression(castContent), [.. pack.Expressions.Keys]);

        // A meeting agreed in the reaction is held as a promise, if the story can hold it.
        string? agreed = null;
        if (owner is not null
            && MeetingAgreement.ToPromise(reaction.Meet, owner.Id.ToString(), scene.Clock, setting.Days, known.Select(p => (p.Id, p.Name))) is { } promise
            && (await story.GetPromisesAsync(saveId, openOnly: true, ct).ConfigureAwait(false)).All(p => p.CharacterId != promise.CharacterId))
        {
            await story.AddPromiseAsync(saveId, promise, ct).ConfigureAwait(false);
            var placeName = known.First(p => p.Id == promise.PlaceId).Name;
            agreed = $"You agreed to meet {owner.Name} at {placeName} on day {promise.DueDay}, {promise.DueSlot.ToString()!.ToLowerInvariant()}.";
        }

        return new ReactionResult(
            new SceneView(reaction.Text, owner?.Id, owner?.Name, owner?.Member.Aesthetic, expression),
            popup,
            agreed);
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
    private async Task EnsureBibleAsync(SaveId saveId, SettingDefinition setting, IReadOnlyList<LoveInterest> cast, CancellationToken ct)
    {
        var facts = await story.GetFactsAsync(saveId, ct).ConfigureAwait(false);
        if (cast.Count == 0 || facts.Any(f => f.Fact.Source is BibleWriter.Source or "appearance"))
        {
            return;
        }

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
        SettingDefinition setting,
        IReadOnlyList<LoveInterest> cast,
        IReadOnlyDictionary<string, string> flags)
    {
        var gone = cast.Where(li => HasLeft(flags, li)).Select(li => li.Ref).ToHashSet(StringComparer.Ordinal);
        var all = encounters.For(setting.Id);

        return gone.Count == 0 ? all : [.. all.Where(e => !(e.With ?? []).Any(gone.Contains))];
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
            var picked = routes.PickNames(main.Appearance.Subject, variants.Count, identities.Select(i => i.Name).OfType<string>(), main.AnchorSeed ?? 0);

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
    /// A love interest's week, anchored where the story put them: the main LI at their home place in
    /// the opening's slot, the routine variant at the routine place in the mornings, the others where
    /// they were met in the evenings.
    /// </summary>
    private static CharacterSchedule ScheduleFor(SaveId saveId, SettingDefinition setting, LoveInterest li, IReadOnlyDictionary<string, string> flags)
    {
        string? place;
        TimeOfDay? slot;

        if (li.Key == JsonEncounterCatalog.MainLiRef)
        {
            place = flags.GetValueOrDefault("main_li.home_place");
            slot = setting.Openings.FirstOrDefault(o => o.Id == flags.GetValueOrDefault("opening"))?.Time;
        }
        else if (li.Key == "routine")
        {
            place = setting.RoutinePlace;
            slot = TimeOfDay.Morning;
        }
        else
        {
            place = flags.GetValueOrDefault($"{li.Key}.place");
            slot = TimeOfDay.Evening;
        }

        var id = li.Id.ToString();
        return ScheduleGenerator.For(id, setting, place, slot, ScheduleGenerator.SeedFor(saveId, id));
    }

    private static string RefFor(string key) =>
        key == JsonEncounterCatalog.MainLiRef ? key : JsonEncounterCatalog.VariantPrefix + key;

    private EncounterDefinition Encounter(SettingDefinition setting, string id) =>
        encounters.For(setting.Id).FirstOrDefault(e => e.Id == id)
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

        if (Has("main_li.contact") && !Has("main_li.first_date") && clock.Day < JsonEncounterCatalog.FirstDateDay)
        {
            return [$"You have {mainLi}'s number. Ask them somewhere from day {JsonEncounterCatalog.FirstDateDay}."];
        }

        return [];
    }

    /// <summary>Whether some encounter would honour inviting <paramref name="key"/> at a place the player knows, now.</summary>
    private bool CanInvite(
        SettingDefinition setting,
        IReadOnlyList<LoveInterest> cast,
        IReadOnlyList<PlaceRecord> known,
        IReadOnlyDictionary<string, string> flags,
        ClockState clock,
        string key)
    {
        var requirement = $"{EncounterEvaluator.InviteKey}={key}";
        var withInvite = new Dictionary<string, string>(flags, StringComparer.Ordinal) { [EncounterEvaluator.InviteKey] = key };
        var inviteBeats = Available(setting, cast, flags).Where(e => (e.Requires ?? []).Contains(requirement)).ToList();

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
