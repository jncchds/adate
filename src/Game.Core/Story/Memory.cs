namespace Game.Core.Story;

public enum MemoryScope
{
    Scene,
    Day,
    Week,
}

/// <summary>A short summary of something that happened (plan §8), with who was there and why it mattered.</summary>
/// <param name="People">Character ids who shared it with the player.</param>
/// <param name="Tags">Salience tags from <see cref="MemoryTags"/>.</param>
/// <param name="Day">The day it happened; for a day or week summary, its last day.</param>
/// <param name="Embedding">The summary's embedding, or null when no embedding service answered.</param>
/// <param name="CompactedInto">The day or week summary this one was folded into; null while it stands alone.</param>
public sealed record MemoryEntry(
    long Id,
    MemoryScope Scope,
    int Day,
    string Summary,
    IReadOnlyList<string> People,
    IReadOnlyList<string> Tags,
    float[]? Embedding,
    long? CompactedInto = null);

public static class MemoryTags
{
    public const string First = "first";
    public const string Conflict = "conflict";

    /// <summary>The vocabulary a scene may tag itself with.</summary>
    public static readonly IReadOnlyList<string> All = [First, Conflict, "date", "confession", "promise", "gift", "secret"];

    /// <summary>Plan §8: memories tagged first or conflict are never compacted.</summary>
    public static bool Keep(MemoryEntry memory) =>
        memory.Tags.Contains(First, StringComparer.Ordinal) || memory.Tags.Contains(Conflict, StringComparer.Ordinal);
}

/// <summary>What the writer remembers for a scene, and what gets compacted when. Pure.</summary>
public static class MemoryRetrieval
{
    public const int DaysPerWeek = 7;

    /// <summary>Each day back costs this much similarity, so a close match from last month still beats a vague one from today.</summary>
    public const double RecencyWeight = 0.01;

    public static double Cosine(float[] a, float[] b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.Length != b.Length || a.Length == 0)
        {
            return 0;
        }

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    /// <summary>The most recent standing scene shared with anyone present.</summary>
    public static MemoryEntry? LastShared(IEnumerable<MemoryEntry> memories, IReadOnlyCollection<string> present) =>
        Standing(memories)
            .Where(m => m.Scope is MemoryScope.Scene && m.People.Any(present.Contains))
            .OrderByDescending(m => m.Day)
            .ThenByDescending(m => m.Id)
            .FirstOrDefault();

    /// <summary>
    /// The standing memories involving someone present, most similar to <paramref name="query"/> first,
    /// less recent ones discounted. Without a query embedding, simply the most recent.
    /// </summary>
    public static IReadOnlyList<MemoryEntry> Retrieve(
        IEnumerable<MemoryEntry> memories,
        float[]? query,
        IReadOnlyCollection<string> present,
        int today,
        int top,
        long? exclude = null)
    {
        ArgumentNullException.ThrowIfNull(memories);
        ArgumentNullException.ThrowIfNull(present);

        var candidates = Standing(memories)
            .Where(m => m.Id != exclude && m.Scope is not MemoryScope.Week && m.People.Any(present.Contains));

        return
        [
            .. candidates
                .Select(m => (Memory: m, Score: (query is null || m.Embedding is null ? 0 : Cosine(query, m.Embedding)) - RecencyWeight * (today - m.Day)))
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Memory.Id)
                .Take(top)
                .Select(x => x.Memory),
        ];
    }

    /// <summary>The summary of the last full week before today, if one exists.</summary>
    public static MemoryEntry? LastWeek(IEnumerable<MemoryEntry> memories, int today) =>
        Standing(memories)
            .Where(m => m.Scope is MemoryScope.Week && m.Day < today)
            .OrderByDescending(m => m.Day)
            .FirstOrDefault();

    /// <summary>
    /// What to compact now: scenes from earlier days into one summary per day, and day summaries
    /// from earlier weeks into one per week. Memories tagged first or conflict stay as they are;
    /// a group of fewer than two is left alone.
    /// </summary>
    public static IReadOnlyList<(MemoryScope Into, int Day, IReadOnlyList<MemoryEntry> Members)> CompactionGroups(
        IEnumerable<MemoryEntry> memories,
        int today)
    {
        ArgumentNullException.ThrowIfNull(memories);

        var standing = Standing(memories).Where(m => !MemoryTags.Keep(m)).ToList();
        var currentWeek = (today - 1) / DaysPerWeek;

        var days = standing
            .Where(m => m.Scope is MemoryScope.Scene && m.Day < today)
            .GroupBy(m => m.Day)
            .Where(g => g.Count() >= 2)
            .Select(g => (MemoryScope.Day, g.Key, (IReadOnlyList<MemoryEntry>)[.. g.OrderBy(m => m.Id)]));

        var weeks = standing
            .Where(m => m.Scope is MemoryScope.Day && (m.Day - 1) / DaysPerWeek < currentWeek)
            .GroupBy(m => (m.Day - 1) / DaysPerWeek)
            .Where(g => g.Count() >= 2)
            .Select(g => (MemoryScope.Week, g.Max(m => m.Day), (IReadOnlyList<MemoryEntry>)[.. g.OrderBy(m => m.Day)]));

        return [.. days, .. weeks];
    }

    private static IEnumerable<MemoryEntry> Standing(IEnumerable<MemoryEntry> memories) =>
        memories.Where(m => m.CompactedInto is null);
}
