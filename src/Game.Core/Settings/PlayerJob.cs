using Game.Core.Scenes;

namespace Game.Core.Settings;

/// <summary>
/// The player's job in a setting: a place and shifts at set times, so the week has structure and something to
/// do between meetings. Working a shift builds <see cref="Trait"/>; missing one costs a little stability.
/// </summary>
/// <param name="Title">What the player is, as the writing says it: "a junior analyst at Meridian &amp; Co.".</param>
/// <param name="Place">A place id of the setting, known from the start.</param>
/// <param name="Slots">The times of day a shift runs.</param>
/// <param name="Weekdays">0-6, where day 1 of the story is weekday 0.</param>
/// <param name="Trait">A desire id: what working shows.</param>
/// <param name="Scene">What a shift is, as the writer is told it.</param>
/// <param name="Habit">The job after the player's name: "works as a junior analyst at Meridian &amp; Co.".</param>
public sealed record PlayerJob(
    string Title,
    string Place,
    IReadOnlyList<TimeOfDay> Slots,
    IReadOnlyList<int> Weekdays,
    string Trait,
    string Scene,
    string Habit);
