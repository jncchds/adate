using System.Text.Json;
using System.Text.RegularExpressions;
using Game.Core.World;

namespace Game.Core.Story;

/// <summary>Why a character walks away (plan §9).</summary>
public enum LeaveReason
{
    Dealbreaker,
    Suspicion,
    BrokenPromises,
    Neglect,
}

public enum EndingKind
{
    /// <summary>The player stays with someone.</summary>
    Together,

    /// <summary>The player chose to leave on their own while someone was still on offer.</summary>
    Alone,

    /// <summary>No one was on offer, because of what the characters did, not only the player.</summary>
    LeftAlone,
}

/// <param name="SuspicionTolerance">Suspicion at which a character leaves, divided by their temper's suspicion scale.</param>
/// <param name="NeglectDays">Days without a scene together after which someone still at acquaintance gives up.</param>
public sealed record LeavingRules(int SuspicionTolerance, int NeglectDays, int BrokenPromises, bool DealbreakerLeaves);

public sealed record EndingTexts(string Together, string Alone, string LeftAlone);

/// <summary>The leaving rules and ending texts: content that C# evaluates (plan §9).</summary>
public sealed partial record EndingContent(LeavingRules Leaving, EndingTexts Texts, IReadOnlyDictionary<string, string> Reasons)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static EndingContent Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Ending content '{path}' not found.", path);
        }

        var content = JsonSerializer.Deserialize<EndingContent>(File.ReadAllText(path), Json)
            ?? throw new InvalidOperationException($"Ending content '{path}' deserialised to null.");

        content.Validate();
        return content;
    }

    public string ReasonText(LeaveReason reason) => Reasons[reason.ToString()];

    public void Validate()
    {
        if (Leaving.SuspicionTolerance <= 0 || Leaving.NeglectDays <= 0 || Leaving.BrokenPromises <= 0)
        {
            throw new InvalidOperationException("Leaving thresholds must be positive.");
        }

        foreach (var reason in Enum.GetValues<LeaveReason>())
        {
            if (!Reasons.TryGetValue(reason.ToString(), out var text) || string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidOperationException($"Ending content has no text for leaving because of {reason}.");
            }
        }

        string[] texts = [Texts.Together, Texts.Alone, Texts.LeftAlone, .. Reasons.Values];
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidOperationException("Ending content has an empty text.");
            }

            foreach (Match token in Token().Matches(text))
            {
                if (token.Value != "{who}")
                {
                    throw new InvalidOperationException($"Ending text uses '{token.Value}'; only {{who}} is filled in.");
                }
            }
        }
    }

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex Token();
}

/// <summary>Where one love interest stands when the day ends or the story does.</summary>
/// <param name="Key">Flag prefix: <c>main_li</c> or a route id.</param>
/// <param name="LeftFor">The reason they left, or null if they have not.</param>
/// <param name="LastSeenDay">The last day the player shared a scene with them.</param>
public sealed record RouteStatus(
    string Key,
    bool Met,
    string? LeftFor,
    RelationshipState State,
    IReadOnlyDictionary<string, string> Temper,
    int? LastSeenDay,
    int BrokenPromises)
{
    /// <summary>Met and still around. A closed route stays closed.</summary>
    public bool Open => Met && LeftFor is null;
}

/// <summary>The ending the player picked, as the rules allow it.</summary>
public sealed record EndingChoice(EndingKind Kind, string? Key);

/// <summary>
/// Leaving, the ending check and the ending itself (plan §9), all pure so every combination of open
/// routes and picks can be tested.
/// </summary>
public sealed class EndingRules(EndingContent content, StoryContent story)
{
    /// <summary>What the player picks to end the story on their own.</summary>
    public const string AloneKey = "alone";

    /// <summary>
    /// Whether someone walks away tonight, and why: a dealbreaker first, then suspicion past what
    /// their temper tolerates, then broken promises, then neglect while still an acquaintance.
    /// </summary>
    public LeaveReason? Leaving(RouteStatus status, int today)
    {
        ArgumentNullException.ThrowIfNull(status);

        if (!status.Open)
        {
            return null;
        }

        var rules = content.Leaving;

        if (rules.DealbreakerLeaves && status.State.Dealbreaker)
        {
            return LeaveReason.Dealbreaker;
        }

        if (status.State.Suspicion >= SuspicionToleranceFor(status.Temper))
        {
            return LeaveReason.Suspicion;
        }

        if (status.BrokenPromises >= BrokenPromisesFor(status.Temper))
        {
            return LeaveReason.BrokenPromises;
        }

        if (status.State.Stage is RelationshipStage.Acquaintance
            && status.LastSeenDay is { } seen
            && today - seen >= NeglectDaysFor(status.Temper))
        {
            return LeaveReason.Neglect;
        }

        return null;
    }

    /// <summary>A fiery temper tolerates less suspicion than a calm one.</summary>
    public int SuspicionToleranceFor(IReadOnlyDictionary<string, string> temper) =>
        (int)Math.Ceiling(content.Leaving.SuspicionTolerance / story.TemperScale(temper, m => m.Suspicion));

    /// <summary>Days of neglect an acquaintance waits through: longer for a patient temper, never under one.</summary>
    public int NeglectDaysFor(IReadOnlyDictionary<string, string> temper) =>
        Math.Max(1, (int)Math.Round(content.Leaving.NeglectDays * story.TemperScale(temper, m => m.Patience), MidpointRounding.AwayFromZero));

    /// <summary>Broken promises forgiven before leaving: more for a patient temper, never under one.</summary>
    public int BrokenPromisesFor(IReadOnlyDictionary<string, string> temper) =>
        Math.Max(1, (int)Math.Round(content.Leaving.BrokenPromises * story.TemperScale(temper, m => m.Patience), MidpointRounding.AwayFromZero));

    /// <summary>On the last day, or earlier once a single route is open and it has reached committed.</summary>
    public static bool IsDue(ClockState clock, int days, IReadOnlyList<RouteStatus> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        if (clock.IsPast(days))
        {
            return true;
        }

        var open = routes.Where(r => r.Open).ToList();
        return open.Count == 1 && open[0].State.Stage is RelationshipStage.Committed;
    }

    /// <summary>The routes the player may end with: open and at <c>dating</c> or above. Alone is always on offer too.</summary>
    public static IReadOnlyList<string> Offer(IReadOnlyList<RouteStatus> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        return [.. routes.Where(r => r.Open && r.State.Stage >= RelationshipStage.Dating).Select(r => r.Key)];
    }

    /// <summary>
    /// The ending for a pick. Alone is always allowed; it counts as the characters' doing when no one
    /// was on offer and someone had left. Picking a route that is not on offer is refused.
    /// </summary>
    public static EndingChoice Resolve(IReadOnlyList<RouteStatus> routes, string pick)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentException.ThrowIfNullOrWhiteSpace(pick);

        var offer = Offer(routes);

        if (pick == AloneKey)
        {
            var leftBehind = offer.Count == 0 && routes.Any(r => r.LeftFor is not null);
            return new EndingChoice(leftBehind ? EndingKind.LeftAlone : EndingKind.Alone, null);
        }

        return offer.Contains(pick)
            ? new EndingChoice(EndingKind.Together, pick)
            : throw new InvalidOperationException($"'{pick}' is not on offer. On offer: {string.Join(", ", [.. offer, AloneKey])}.");
    }
}
