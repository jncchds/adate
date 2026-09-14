using Game.Core.Scenes;
using Game.Core.World;

namespace Game.Core.Story;

/// <param name="Weekdays">0-6, where day 1 of the story is weekday 0. Null means every day.</param>
public sealed record ScheduleEntry(TimeOfDay Slot, string PlaceId, IReadOnlyList<int>? Weekdays = null);

/// <summary>
/// A character's weekly template (plan §8). A character is only where their schedule puts them,
/// unless an encounter or a promise overrides it.
/// </summary>
public sealed record CharacterSchedule(string CharacterId, IReadOnlyList<ScheduleEntry> Entries)
{
    public const int WeekLength = 7;

    public static int Weekday(int day) => (day - 1) % WeekLength;

    /// <summary>The place the schedule puts the character at, or null when it puts them nowhere the player can go.</summary>
    public string? Where(ClockState clock) =>
        Entries.FirstOrDefault(e => e.Slot == clock.Slot && (e.Weekdays is null || e.Weekdays.Contains(Weekday(clock.Day))))?.PlaceId;
}

public enum PromiseKind
{
    Meet,
    Call,
    Bring,
    KeepSecret,
    Help,
}

public enum PromiseStatus
{
    Open,
    Kept,
    Broken,
}

/// <param name="DueSlot">For a meeting, the slot; null means any time that day.</param>
/// <param name="PlaceId">For a meeting, where.</param>
public sealed record Promise(
    string Id,
    string CharacterId,
    PromiseKind Kind,
    int MadeDay,
    int DueDay,
    TimeOfDay? DueSlot = null,
    string? PlaceId = null,
    PromiseStatus Status = PromiseStatus.Open);

/// <summary>When promises are kept or broken.</summary>
public static class Promises
{
    public static bool IsDue(Promise promise, ClockState clock) =>
        clock.Day == promise.DueDay && (promise.DueSlot is null || promise.DueSlot == clock.Slot);

    public static bool IsPast(Promise promise, ClockState clock) =>
        clock.Day > promise.DueDay || (clock.Day == promise.DueDay && promise.DueSlot is { } slot && clock.Slot > slot);

    /// <summary>
    /// What a turn at <paramref name="visitedAt"/> does to a promise: a meeting is kept by being at its
    /// place in its slot with the character, and anything still open once its time has passed is broken.
    /// Other kinds are kept by the scenes that fulfil them. Null when nothing changes.
    /// </summary>
    public static PromiseStatus? Resolve(Promise promise, ClockState visitedAt, string placeId, IReadOnlyCollection<string> with)
    {
        ArgumentNullException.ThrowIfNull(promise);
        ArgumentNullException.ThrowIfNull(with);

        if (promise.Status is not PromiseStatus.Open)
        {
            return null;
        }

        if (promise.Kind is PromiseKind.Meet
            && IsDue(promise, visitedAt)
            && promise.PlaceId == placeId
            && with.Contains(promise.CharacterId))
        {
            return PromiseStatus.Kept;
        }

        return IsPast(promise, visitedAt) ? PromiseStatus.Broken : null;
    }

    /// <summary>Whether an open meeting puts the character at this place now, whatever their schedule says.</summary>
    public static bool PutsThere(Promise promise, ClockState clock, string placeId) =>
        promise.Status is PromiseStatus.Open
        && promise.Kind is PromiseKind.Meet
        && IsDue(promise, clock)
        && promise.PlaceId == placeId;
}
