using Game.Core.Encounters;
using Game.Core.Places;
using Game.Core.Saves;
using Game.Core.Settings;
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
    bool CanInvite);

/// <summary>A save's setting, places, clock, openings, choices and turns.</summary>
public sealed class WorldService(
    SaveRepository saves,
    PlaceRepository places,
    CharacterRepository characters,
    GameStateRepository state,
    ISettingCatalog settings,
    IEncounterCatalog encounters,
    IOptions<StudioOptions> options)
{
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
            !over && pending is null && CanInvite(setting, known, flags, clock));
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

        await state.CommitTurnAsync(saveId, outcome, ct).ConfigureAwait(false);

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

        await state.ResolveChoiceAsync(
            saveId, pending.EncounterId, choice.Id, TurnPlanner.Assignments(choice.Sets ?? []), ct).ConfigureAwait(false);
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
