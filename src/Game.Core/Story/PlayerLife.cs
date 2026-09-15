using System.Globalization;
using Game.Core.Settings;
using Game.Core.World;

namespace Game.Core.Story;

/// <summary>
/// What the player does between meetings (user feedback: wandering from place to place waiting for the next
/// morning). What they choose to do on their own and the shifts they work build traits, named after the
/// qualities people look for, and people who value a trait warm to a player who has it. Kept as flags.
/// </summary>
public static class PlayerLife
{
    public const string TraitPrefix = "player.trait.";
    public const string ShiftsWorkedKey = "player.shifts_worked";
    public const string ShiftsMissedKey = "player.shifts_missed";

    /// <summary>The trait a missed shift costs.</summary>
    public const string MissedShiftTrait = "stability";

    /// <summary>How many times the player has shown each trait.</summary>
    public static IReadOnlyDictionary<string, int> Traits(IReadOnlyDictionary<string, string> flags)
    {
        ArgumentNullException.ThrowIfNull(flags);

        return flags
            .Where(f => f.Key.StartsWith(TraitPrefix, StringComparison.Ordinal))
            .ToDictionary(f => f.Key[TraitPrefix.Length..], f => Count(f.Value), StringComparer.Ordinal);
    }

    /// <summary>How developed a trait shown <paramref name="count"/> times is: the thresholds it has passed.</summary>
    public static int Level(int count, IReadOnlyList<int>? thresholds) => (thresholds ?? []).Count(t => count >= t);

    /// <summary>
    /// The warmth a character feels for who the player has become, once per scene they share: each developed
    /// trait counts by how much the character values it and how developed it is, capped by the rules.
    /// </summary>
    /// <param name="weightOf">How much the character values a desire; 0 for one they do not hold.</param>
    public static int Rapport(Func<string, int> weightOf, IReadOnlyDictionary<string, int> traits, RelationshipRules rules)
    {
        ArgumentNullException.ThrowIfNull(weightOf);
        ArgumentNullException.ThrowIfNull(traits);
        ArgumentNullException.ThrowIfNull(rules);

        if (rules.RapportMax <= 0 || rules.TraitLevels is not { Count: > 0 } levels)
        {
            return 0;
        }

        var sum = traits.Sum(t => weightOf(t.Key) * Level(t.Value, levels));
        return Math.Min(rules.RapportMax, sum / 3);
    }

    /// <summary>Whether the player's shift is now.</summary>
    public static bool OnShift(PlayerJob? job, ClockState clock) =>
        job is not null && job.Slots.Contains(clock.Slot) && job.Weekdays.Contains(CharacterSchedule.Weekday(clock.Day));

    /// <summary>Adds one showing of each of <paramref name="traits"/>.</summary>
    public static void Show(Dictionary<string, string> toSet, IReadOnlyDictionary<string, string> flags, IEnumerable<string> traits)
    {
        ArgumentNullException.ThrowIfNull(traits);

        foreach (var trait in traits.Distinct(StringComparer.Ordinal))
        {
            Increment(toSet, flags, TraitPrefix + trait, 1);
        }
    }

    /// <summary>Adds what working a shift of <paramref name="job"/> changes.</summary>
    public static void WorkShift(Dictionary<string, string> toSet, IReadOnlyDictionary<string, string> flags, PlayerJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        Increment(toSet, flags, ShiftsWorkedKey, 1);
        Increment(toSet, flags, TraitPrefix + job.Trait, 1);
    }

    /// <summary>Adds what missing a shift costs.</summary>
    public static void MissShift(Dictionary<string, string> toSet, IReadOnlyDictionary<string, string> flags)
    {
        Increment(toSet, flags, ShiftsMissedKey, 1);
        Increment(toSet, flags, TraitPrefix + MissedShiftTrait, -1);
    }

    private static int Count(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : 0;

    private static void Increment(Dictionary<string, string> toSet, IReadOnlyDictionary<string, string> flags, string key, int by)
    {
        ArgumentNullException.ThrowIfNull(toSet);
        ArgumentNullException.ThrowIfNull(flags);

        var current = toSet.TryGetValue(key, out var pending) ? Count(pending) : Count(flags.GetValueOrDefault(key));
        toSet[key] = Math.Max(0, current + by).ToString(CultureInfo.InvariantCulture);
    }
}
