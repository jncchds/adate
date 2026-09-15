using System.Globalization;
using Game.Core.Scenes;
using Game.Core.World;

namespace Game.Core.Story;

/// <summary>One part of someone's week: when (weekdays, the weekend or every day, and the time of day) and where.</summary>
public sealed record RoutineEntry(string Days, TimeOfDay Slot, string PlaceId);

/// <summary>
/// What the player knows of the people's weeks (user request: a way to learn where to find someone the rest of the
/// day). Their schedule is derived; what the player knows of it is kept in flags: the place and time they first met
/// someone, their home once they know it, what someone told them about their week, and what the player noticed by
/// finding them in the same place at the same time twice.
/// </summary>
public static class RoutineKnowledge
{
    public const string Weekdays = "weekdays";
    public const string Weekend = "weekend";
    public const string Daily = "daily";

    /// <summary>Finding someone where their week puts them this many times teaches that part of it.</summary>
    public const int SightingsToLearn = 2;

    public static string DaysOf(IReadOnlyList<int>? weekdays) =>
        weekdays is not { Count: > 0 and < 7 } ? Daily
        : weekdays.All(d => d < 5) ? Weekdays
        : weekdays.All(d => d >= 5) ? Weekend
        : Daily;

    /// <summary>A schedule as parts of a week.</summary>
    public static IReadOnlyList<RoutineEntry> Entries(CharacterSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        return [.. schedule.Entries.Select(e => new RoutineEntry(DaysOf(e.Weekdays), e.Slot, e.PlaceId)).Distinct()];
    }

    public static string LearnedKey(string key, RoutineEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return $"{key}.routine.{entry.Days}.{entry.Slot.ToString().ToLowerInvariant()}";
    }

    public static string SeenKey(string key, RoutineEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return $"{key}.seen.{entry.Days}.{entry.Slot.ToString().ToLowerInvariant()}.{entry.PlaceId}";
    }

    /// <summary>The parts of a week the player knows: learned ones that still hold, and the ones given (where they met, their home).</summary>
    public static IReadOnlyList<RoutineEntry> Known(
        CharacterSchedule schedule, string key, IReadOnlyDictionary<string, string> flags, IEnumerable<RoutineEntry> given)
    {
        ArgumentNullException.ThrowIfNull(flags);
        var granted = given.ToHashSet();
        return [.. Entries(schedule).Where(e => granted.Contains(e) || flags.GetValueOrDefault(LearnedKey(key, e)) == e.PlaceId)];
    }

    /// <summary>Whether a part of a week covers this day and time.</summary>
    public static bool Fits(RoutineEntry entry, ClockState clock)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var weekday = CharacterSchedule.Weekday(clock.Day);
        return entry.Slot == clock.Slot && entry.Days switch
        {
            Weekdays => weekday < 5,
            Weekend => weekday >= 5,
            _ => true,
        };
    }

    /// <summary>The part of the week that puts someone at <paramref name="placeId"/> now, or null when their schedule does not.</summary>
    public static RoutineEntry? Match(CharacterSchedule schedule, ClockState clock, string placeId)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        return schedule.Where(clock) == placeId
            ? Entries(schedule).FirstOrDefault(e => e.PlaceId == placeId && Fits(e, clock))
            : null;
    }

    /// <summary>Counts finding someone where their week puts them, learning that part once it has happened <see cref="SightingsToLearn"/> times.</summary>
    public static void See(Dictionary<string, string> toSet, IReadOnlyDictionary<string, string> flags, string key, RoutineEntry entry)
    {
        ArgumentNullException.ThrowIfNull(toSet);
        ArgumentNullException.ThrowIfNull(flags);

        var seenKey = SeenKey(key, entry);
        var before = toSet.TryGetValue(seenKey, out var pending) ? pending : flags.GetValueOrDefault(seenKey);
        var count = (int.TryParse(before, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0) + 1;

        toSet[seenKey] = count.ToString(CultureInfo.InvariantCulture);
        if (count >= SightingsToLearn)
        {
            toSet[LearnedKey(key, entry)] = entry.PlaceId;
        }
    }

    /// <summary>"weekday mornings at the dock; nights at Asya's cabin".</summary>
    public static string Describe(IEnumerable<RoutineEntry> entries, Func<string, string> placeName)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(placeName);

        return string.Join("; ", entries
            .OrderBy(e => e.Days switch { Weekdays => 0, Weekend => 1, _ => 2 })
            .ThenBy(e => e.Slot)
            .Select(e => $"{DaysWord(e.Days)}{SlotWord(e.Slot)} at {placeName(e.PlaceId)}"));
    }

    private static string DaysWord(string days) => days switch
    {
        Weekdays => "weekday ",
        Weekend => "weekend ",
        _ => "",
    };

    private static string SlotWord(TimeOfDay slot) => slot switch
    {
        TimeOfDay.Morning => "mornings",
        TimeOfDay.Midday => "middays",
        TimeOfDay.Afternoon => "afternoons",
        TimeOfDay.Evening => "evenings",
        _ => "nights",
    };
}
