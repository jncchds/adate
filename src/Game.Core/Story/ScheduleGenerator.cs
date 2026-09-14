using Game.Core.Places;
using Game.Core.Scenes;
using Game.Core.Settings;

namespace Game.Core.Story;

/// <summary>
/// A love interest's weekly schedule (plan §8, phase-3 plan: every turn is written), derived rather
/// than stored, like weather: deterministic from the person's seed and anchored where the story put
/// them, so the place a player learned they can be found keeps being true.
/// </summary>
public static class ScheduleGenerator
{
    /// <summary>Weekdays are 0-4 (day 1 of the story is weekday 0); 5-6 are the weekend.</summary>
    public static readonly IReadOnlyList<int> Weekdays = [0, 1, 2, 3, 4];

    public static readonly IReadOnlyList<int> Weekend = [5, 6];

    /// <summary>How often a free slot has somewhere to be, rather than somewhere the player cannot go.</summary>
    public const double BusyChance = 0.55;

    /// <param name="anchorPlace">Where the story says they are found: a home place, a routine place, where they were met.</param>
    /// <param name="anchorSlot">When, on weekdays.</param>
    /// <param name="seed">The person's own seed, so every save gives them the same week.</param>
    public static CharacterSchedule For(
        string characterId,
        SettingDefinition setting,
        string? anchorPlace,
        TimeOfDay? anchorSlot,
        long seed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(characterId);
        ArgumentNullException.ThrowIfNull(setting);

        var rng = new StoryRng(StoryRng.Mix((ulong)seed, 0x5C4ED));
        var places = setting.Places.Select(p => p.Id).ToList();
        var entries = new List<ScheduleEntry>();

        if (anchorPlace is not null && anchorSlot is { } slot)
        {
            entries.Add(new ScheduleEntry(slot, anchorPlace, Weekdays));
        }

        // Nights are spent at home, somewhere the player never goes.
        TimeOfDay[] waking = [TimeOfDay.Morning, TimeOfDay.Midday, TimeOfDay.Afternoon, TimeOfDay.Evening];

        foreach (var time in waking)
        {
            if (!(anchorSlot == time && anchorPlace is not null) && rng.Below(100) < BusyChance * 100)
            {
                entries.Add(new ScheduleEntry(time, places[rng.Below(places.Count)], Weekdays));
            }

            if (rng.Below(100) < BusyChance * 100)
            {
                entries.Add(new ScheduleEntry(time, places[rng.Below(places.Count)], Weekend));
            }
        }

        return new CharacterSchedule(characterId, entries);
    }

    /// <summary>A stable seed for a person's schedule, from the save and the person.</summary>
    public static long SeedFor(Game.Core.Saves.SaveId saveId, string characterId) =>
        PlaceRecord.SeedFor(saveId, "schedule:" + characterId);
}
