using System.Globalization;
using Game.Core.Content;
using Game.Core.Settings;
using Game.Core.World;

namespace Game.Core.Story;

/// <summary>
/// What the player does between meetings (user feedback: wandering from place to place waiting for the next
/// morning). Activities and work shifts build traits, named after the qualities people look for, and people
/// who value a trait warm to a player who has it. Kept as flags, like every other fact of a save.
/// </summary>
public static class PlayerLife
{
    public const string TraitPrefix = "player.trait.";
    public const string DidPrefix = "player.did.";
    public const string ShiftsWorkedKey = "player.shifts_worked";
    public const string ShiftsMissedKey = "player.shifts_missed";

    /// <summary>The activity id of working a shift, and the place type it is recorded under.</summary>
    public const string ShiftId = "shift";

    public const string JobTypeId = "job";

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

    /// <summary>Working the shift, as an activity at the job's place.</summary>
    public static PlaceActivity ShiftActivity(PlayerJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new PlaceActivity(ShiftId, "Work your shift", job.Trait, job.Scene, job.Habit);
    }

    /// <summary>Adds what doing <paramref name="activity"/> at a place of <paramref name="typeId"/> changes.</summary>
    public static void Record(Dictionary<string, string> toSet, IReadOnlyDictionary<string, string> flags, string typeId, PlaceActivity activity)
    {
        ArgumentNullException.ThrowIfNull(toSet);
        ArgumentNullException.ThrowIfNull(activity);

        Increment(toSet, flags, TraitPrefix + activity.Trait, 1);
        Increment(toSet, flags, $"{DidPrefix}{typeId}.{activity.Id}", 1);
        if (activity.Id == ShiftId)
        {
            Increment(toSet, flags, ShiftsWorkedKey, 1);
        }
    }

    /// <summary>Adds what missing a shift costs.</summary>
    public static void MissShift(Dictionary<string, string> toSet, IReadOnlyDictionary<string, string> flags)
    {
        ArgumentNullException.ThrowIfNull(toSet);

        Increment(toSet, flags, ShiftsMissedKey, 1);
        Increment(toSet, flags, TraitPrefix + MissedShiftTrait, -1);
    }

    /// <summary>What the player does most, most often first, as (place type id, activity id), each done at least <paramref name="atLeast"/> times.</summary>
    public static IReadOnlyList<(string TypeId, string ActivityId)> Pastimes(IReadOnlyDictionary<string, string> flags, int atLeast = 2)
    {
        ArgumentNullException.ThrowIfNull(flags);

        return
        [
            .. flags
                .Where(f => f.Key.StartsWith(DidPrefix, StringComparison.Ordinal) && Count(f.Value) >= atLeast)
                .OrderByDescending(f => Count(f.Value))
                .ThenBy(f => f.Key, StringComparer.Ordinal)
                .Select(f => f.Key[DidPrefix.Length..])
                .Where(k => k.Contains('.', StringComparison.Ordinal))
                .Select(k => (k[..k.IndexOf('.', StringComparison.Ordinal)], k[(k.IndexOf('.', StringComparison.Ordinal) + 1)..])),
        ];
    }

    private static int Count(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : 0;

    private static void Increment(Dictionary<string, string> toSet, IReadOnlyDictionary<string, string> flags, string key, int by)
    {
        var current = toSet.TryGetValue(key, out var pending) ? Count(pending) : Count(flags.GetValueOrDefault(key));
        toSet[key] = Math.Max(0, current + by).ToString(CultureInfo.InvariantCulture);
    }
}
