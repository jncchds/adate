using Game.Core.Scenes;

namespace Game.Core.World;

/// <summary>
/// Where a save is in its calendar: a day and a time slot (phase-2 plan §1). Mirrors
/// <c>game_clock</c>. A turn spends one slot; Night rolls over to the next day's Morning.
/// </summary>
public readonly record struct ClockState(int Day, TimeOfDay Slot)
{
    private static readonly TimeOfDay[] Slots = Enum.GetValues<TimeOfDay>();

    public static ClockState Start => new(1, Slots[0]);

    public ClockState Next()
    {
        var index = Array.IndexOf(Slots, Slot);
        return index == Slots.Length - 1 ? new ClockState(Day + 1, Slots[0]) : new ClockState(Day, Slots[index + 1]);
    }

    /// <summary>Whether the calendar of <paramref name="days"/> days has run out.</summary>
    public bool IsPast(int days) => Day > days;
}
