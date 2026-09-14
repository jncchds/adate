using Game.Core.Encounters;
using Game.Core.Places;
using Game.Core.Saves;
using Game.Core.Settings;
using Game.Core.World;
using Game.Data.Repositories;
using Microsoft.Extensions.Options;

namespace Game.Host.Services;

/// <param name="Today">Setting events held today, whose places are known for the day.</param>
public sealed record PlayState(
    SettingDefinition Setting,
    ClockState Clock,
    IReadOnlyList<PlaceRecord> KnownPlaces,
    IReadOnlyList<SettingEvent> Today,
    bool Over);

/// <summary>A save's setting, places, clock and turns.</summary>
public sealed class WorldService(
    SaveRepository saves,
    PlaceRepository places,
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
    /// Where the save stands: its day and slot, the places the player knows, and today's events.
    /// Starts the clock on a save that has none. An event's place becomes known on the event day.
    /// </summary>
    public async Task<PlayState> GetPlayStateAsync(SaveId saveId, CancellationToken ct = default)
    {
        var setting = await EnsureSettingAsync(saveId, ct).ConfigureAwait(false);
        var clock = await state.GetOrStartClockAsync(saveId, ct).ConfigureAwait(false);

        foreach (var placeId in TurnPlanner.DayStartReveals(setting, clock.Day))
        {
            await places.MarkKnownAsync(saveId, placeId, clock.Day, ct).ConfigureAwait(false);
        }

        return new PlayState(
            setting,
            clock,
            await ListKnownAsync(saveId, ct).ConfigureAwait(false),
            [.. setting.Events.Where(e => e.Day == clock.Day)],
            clock.IsPast(setting.Days));
    }

    /// <summary>Spends the current slot at <paramref name="placeId"/>, which must be a place the player knows.</summary>
    public async Task<TurnOutcome> TakeTurnAsync(SaveId saveId, string placeId, CancellationToken ct = default)
    {
        var play = await GetPlayStateAsync(saveId, ct).ConfigureAwait(false);

        if (play.Over)
        {
            throw new InvalidOperationException($"The {play.Setting.Days} days of this save are over.");
        }

        var place = play.KnownPlaces.FirstOrDefault(p => p.Id == placeId)
            ?? throw new InvalidOperationException($"'{placeId}' is not a place the player knows.");

        var context = new TurnContext(
            play.Clock,
            place.Id,
            await state.GetFlagsAsync(saveId, ct).ConfigureAwait(false),
            await state.CountAloneVisitsAsync(saveId, place.Id, ct).ConfigureAwait(false));

        var outcome = TurnPlanner.Plan(play.Setting, encounters.For(play.Setting.Id), context, place.Name);

        await state.CommitTurnAsync(saveId, outcome, ct).ConfigureAwait(false);
        return outcome;
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
