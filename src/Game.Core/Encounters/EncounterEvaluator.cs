using System.Globalization;
using Game.Core.Settings;
using Game.Core.World;

namespace Game.Core.Encounters;

/// <param name="AloneVisitsHere">Earlier visits to this place with no one else there, not counting this one.</param>
public sealed record TurnContext(
    ClockState Clock,
    string PlaceId,
    IReadOnlyDictionary<string, string> Flags,
    int AloneVisitsHere);

/// <summary>Everything a turn changes, decided before anything is written.</summary>
/// <param name="VisitedAt">The slot the visit happened in. Its background is drawn at this time of day.</param>
/// <param name="Next">Where the clock moves to.</param>
/// <param name="EncounterId">The encounter that fired, or null for an ordinary slot.</param>
/// <param name="GameOver">Whether this was the last slot of the calendar.</param>
public sealed record TurnOutcome(
    ClockState VisitedAt,
    ClockState Next,
    string PlaceId,
    string? EncounterId,
    string Text,
    IReadOnlyDictionary<string, string> FlagsToSet,
    IReadOnlyList<string> Reveals,
    IReadOnlyList<string> With,
    bool GameOver,
    IReadOnlyList<EncounterChoice>? Choices = null);

/// <summary>Picks the encounter for a turn. Pure: the same state always picks the same encounter.</summary>
public static class EncounterEvaluator
{
    /// <summary>Flags recording that a once-only encounter has fired, holding the day it did.</summary>
    public const string FiredPrefix = "encounter.";

    public static string FiredKey(string encounterId) => FiredPrefix + encounterId;

    /// <summary>Holds the id of an encounter whose choice is still open, or <c>false</c>.</summary>
    public const string PendingChoiceKey = "pending.choice";

    /// <summary>Records which answer the player gave to an encounter's choice.</summary>
    public static string ChoiceKey(string encounterId) => "choice." + encounterId;

    /// <summary>A transient flag, never stored: the player chose to bring the main LI along this turn.</summary>
    public const string InviteKey = "invite";

    /// <summary>The matching encounter with the highest priority; ties go to the lowest id, so the pick never depends on file order.</summary>
    public static EncounterDefinition? Pick(IEnumerable<EncounterDefinition> encounters, TurnContext context)
    {
        ArgumentNullException.ThrowIfNull(encounters);
        ArgumentNullException.ThrowIfNull(context);

        return encounters
            .Where(e => Matches(e, context))
            .OrderByDescending(e => e.Priority)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    public static bool Matches(EncounterDefinition encounter, TurnContext context)
    {
        ArgumentNullException.ThrowIfNull(encounter);
        ArgumentNullException.ThrowIfNull(context);

        var place = encounter.Place;

        if (place.Id is not null && place.Id != context.PlaceId)
        {
            return false;
        }

        if (place.PlaceFlag is not null &&
            (!context.Flags.TryGetValue(place.PlaceFlag, out var flagged) || flagged != context.PlaceId))
        {
            return false;
        }

        if (place.AloneVisitsBefore is { } alone && context.AloneVisitsHere < alone)
        {
            return false;
        }

        if (encounter.Time is { Count: > 0 } time && !time.Contains(context.Clock.Slot))
        {
            return false;
        }

        if (encounter.Days is [var first, var last] && (context.Clock.Day < first || context.Clock.Day > last))
        {
            return false;
        }

        if (encounter.Once && context.Flags.ContainsKey(FiredKey(encounter.Id)))
        {
            return false;
        }

        return (encounter.Requires ?? []).All(expression => Holds(context.Flags, expression));
    }

    /// <summary><c>key</c> holds when set to anything but <c>false</c>; <c>!key</c> is its opposite; <c>key=value</c> compares.</summary>
    public static bool Holds(IReadOnlyDictionary<string, string> flags, string expression)
    {
        ArgumentNullException.ThrowIfNull(flags);
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);

        if (expression.StartsWith('!'))
        {
            return !Holds(flags, expression[1..]);
        }

        var equals = expression.IndexOf('=');
        if (equals >= 0)
        {
            return flags.TryGetValue(expression[..equals], out var value) && value == expression[(equals + 1)..];
        }

        return flags.TryGetValue(expression, out var set) && set != "false";
    }
}

/// <summary>Turns a choice of place into a <see cref="TurnOutcome"/>.</summary>
public static class TurnPlanner
{
    /// <summary>A flag value that stores the place the encounter fired at, for places decided in play.</summary>
    public const string PlaceValue = "{place}";

    public static TurnOutcome Plan(
        SettingDefinition setting,
        IReadOnlyList<EncounterDefinition> encounters,
        TurnContext context,
        string placeName)
    {
        ArgumentNullException.ThrowIfNull(setting);
        ArgumentNullException.ThrowIfNull(encounters);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(placeName);

        if (context.Clock.IsPast(setting.Days))
        {
            throw new InvalidOperationException($"Setting '{setting.Id}' lasts {setting.Days} days; there are no turns left.");
        }

        var encounter = EncounterEvaluator.Pick(encounters, context);
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);

        if (encounter is not null)
        {
            foreach (var (key, value) in Assignments(encounter.Sets ?? []))
            {
                flags[key] = value == PlaceValue ? context.PlaceId : value;
            }

            flags[EncounterEvaluator.FiredKey(encounter.Id)] = context.Clock.Day.ToString(CultureInfo.InvariantCulture);

            if (encounter.Choices is { Count: > 0 })
            {
                flags[EncounterEvaluator.PendingChoiceKey] = encounter.Id;
            }
        }

        var next = context.Clock.Next();

        return new TurnOutcome(
            context.Clock,
            next,
            context.PlaceId,
            encounter?.Id,
            encounter?.Text ?? Ambient(placeName, context.Clock),
            flags,
            encounter?.Reveals ?? [],
            encounter?.With ?? [],
            next.IsPast(setting.Days),
            encounter?.Choices ?? []);
    }

    /// <summary><c>key</c> sets <c>true</c>; <c>key=value</c> sets the value.</summary>
    public static IReadOnlyDictionary<string, string> Assignments(IEnumerable<string> sets)
    {
        ArgumentNullException.ThrowIfNull(sets);

        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var set in sets)
        {
            var equals = set.IndexOf('=');
            if (equals >= 0)
            {
                flags[set[..equals]] = set[(equals + 1)..];
            }
            else
            {
                flags[set] = "true";
            }
        }

        return flags;
    }

    /// <summary>Places that become known on <paramref name="day"/> because a dated event is held there.</summary>
    public static IReadOnlyList<string> DayStartReveals(SettingDefinition setting, int day)
    {
        ArgumentNullException.ThrowIfNull(setting);
        return [.. setting.Events.Where(e => e.Day == day).Select(e => e.Place).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Placeholder for an ordinary slot, until scenes are written (build step 9).</summary>
    public static string Ambient(string placeName, ClockState clock) =>
        $"You spend the {clock.Slot.ToString().ToLowerInvariant()} at {placeName}. Nothing out of the ordinary happens.";
}
