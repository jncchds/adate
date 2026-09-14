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

/// <summary>Someone the player can bring along this turn.</summary>
/// <param name="Key">The invite value and flag prefix: <c>main_li</c> or a route id.</param>
public sealed record Invitee(string Key, string Name);

/// <summary>Where the player stands with someone they have met.</summary>
public sealed record RelationshipView(string Name, RelationshipState State);

/// <param name="Today">Setting events held today, whose places are known for the day.</param>
/// <param name="Opening">The opening the player chose, or null if they have not chosen yet.</param>
/// <param name="Hints">Where the story expects the player to look next, while the opening's beats are open.</param>
/// <param name="Pending">A choice the player must answer before the next turn.</param>
/// <param name="Invitees">Who the player may bring along this turn.</param>
/// <param name="People">Display names by encounter reference: <c>main_li</c> or <c>variant:{route}</c>.</param>
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
    IReadOnlyDictionary<string, string> People);

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
    CastContent castContent,
    RouteContent routes,
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
        var cast = await CastAsync(saveId, setting, ct).ConfigureAwait(false);
        var names = await NamesAsync(saveId, cast, ct).ConfigureAwait(false);

        var opening = flags.TryGetValue("opening", out var openingId)
            ? setting.Openings.FirstOrDefault(o => o.Id == openingId)
            : null;

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

        IReadOnlyList<Invitee> invitees = over || pending is not null
            ? []
            : [.. cast.Where(li => CanInvite(setting, known, flags, clock, li.Key)).Select(li => new Invitee(li.Key, li.Name))];

        var relationships = new List<RelationshipView>();
        foreach (var li in cast.Where(li => EncounterEvaluator.Holds(flags, $"{li.Key}.met")))
        {
            relationships.Add(new RelationshipView(li.Name, await story.GetRelationshipAsync(saveId, li.Id, ct).ConfigureAwait(false)));
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
            people);
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
    /// optionally bringing someone along by their <see cref="Invitee.Key"/>.
    /// </summary>
    public async Task<TurnOutcome> TakeTurnAsync(SaveId saveId, string placeId, string? invite = null, CancellationToken ct = default)
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

        var context = new TurnContext(
            play.Clock,
            place.Id,
            flags,
            await state.CountAloneVisitsAsync(saveId, place.Id, ct).ConfigureAwait(false));

        var outcome = TurnPlanner.Plan(play.Setting, encounters.For(play.Setting.Id), context, place.Name);

        if (invite is not null && !outcome.With.Contains(RefFor(invite)))
        {
            throw new InvalidOperationException($"Nothing at {place.Name} would bring them along.");
        }

        var cast = await CastAsync(saveId, play.Setting, ct).ConfigureAwait(false);

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
        foreach (var li in cast.Where(li => outcome.With.Contains(li.Ref)))
        {
            var current = await story.GetRelationshipAsync(saveId, li.Id, ct).ConfigureAwait(false);
            if (outcome.EncounterId == JsonEncounterCatalog.FirstDateIdFor(li.Key))
            {
                current = _engine.DateAt(current, li.Profile, place.TypeId, play.Clock.Day);
            }

            relationships[li.Id] = Advance(li, current, after, toSet);
        }

        outcome = outcome with { FlagsToSet = toSet };
        await state.CommitTurnAsync(saveId, outcome, relationships, ct).ConfigureAwait(false);

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
        foreach (var li in cast.Where(li => (encounter.With ?? []).Contains(li.Ref)))
        {
            var current = await story.GetRelationshipAsync(saveId, li.Id, ct).ConfigureAwait(false);
            var delta = _engine.Score(li.Profile, li.Member.Temper, li.Member.WantId, tags);
            relationships[li.Id] = Advance(li, _engine.Apply(current, delta, play.Clock.Day), after, sets);
        }

        await state.ResolveChoiceAsync(saveId, pending.EncounterId, choice.Id, sets, relationships, ct).ConfigureAwait(false);
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
        IReadOnlyList<PlaceRecord> known,
        IReadOnlyDictionary<string, string> flags,
        ClockState clock,
        string key)
    {
        var requirement = $"{EncounterEvaluator.InviteKey}={key}";
        var withInvite = new Dictionary<string, string>(flags, StringComparer.Ordinal) { [EncounterEvaluator.InviteKey] = key };
        var inviteBeats = encounters.For(setting.Id).Where(e => (e.Requires ?? []).Contains(requirement)).ToList();

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
