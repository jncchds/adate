using Game.Core.Scenes;
using Game.Core.World;

namespace Game.Core.Story;

/// <summary>
/// The people of the story living their own days (user feedback: a love interest should sometimes simply be
/// somewhere the player goes). Their schedule says where they usually are; now and then they are out somewhere
/// else. Someone the player has not met yet can be run into by chance, which takes the turn from what was
/// planned. Every roll is the same for the same save, person and slot, so reloading changes nothing.
/// </summary>
public static class WorldMoves
{
    /// <summary>How often someone is not where their schedule says, but at some other place.</summary>
    public const double WanderChance = 0.3;

    /// <summary>The chance of running into someone not yet met who happens to be at the same place.</summary>
    public const double MeetHereChance = 0.35;

    /// <summary>The chance of running into someone not yet met anywhere else: a stranger passing through.</summary>
    public const double MeetAnywhereChance = 0.05;

    /// <summary>The first day a chance meeting can happen: day 1 belongs to the opening.</summary>
    public const int FirstChanceDay = 2;

    /// <summary>
    /// Where someone is now: usually where their schedule puts them, sometimes another of
    /// <paramref name="places"/>, and sometimes out somewhere in a slot the schedule leaves free.
    /// </summary>
    public static string? Where(string saveKey, CharacterSchedule schedule, IReadOnlyList<string> places, ClockState clock)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(places);

        // Nights are spent at home, somewhere the player never goes.
        if (clock.Slot is TimeOfDay.Night || places.Count == 0
            || Initiative.Unit($"{saveKey}|{schedule.CharacterId}|{clock.Day}|{clock.Slot}|wander") >= WanderChance)
        {
            return schedule.Where(clock);
        }

        var index = (int)(Initiative.Unit($"{saveKey}|{schedule.CharacterId}|{clock.Day}|{clock.Slot}|where") * places.Count);
        return places[Math.Min(index, places.Count - 1)];
    }

    /// <summary>Whether the player runs into someone not yet met at <paramref name="placeId"/> now.</summary>
    /// <param name="whereNow">Where they are now, from <see cref="Where"/>.</param>
    public static bool MeetsByChance(string saveKey, string key, string? whereNow, string placeId, ClockState clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (clock.Day < FirstChanceDay)
        {
            return false;
        }

        var chance = whereNow == placeId ? MeetHereChance : MeetAnywhereChance;
        return Initiative.Unit($"{saveKey}|{key}|{clock.Day}|{clock.Slot}|chance-meeting") < chance;
    }
}
