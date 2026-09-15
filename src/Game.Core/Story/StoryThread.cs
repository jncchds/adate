namespace Game.Core.Story;

/// <summary>A loose end a scene left open: a question not answered, a plan mentioned, something someone said they would do.</summary>
/// <param name="CharacterId">Whom it is about, or null for the player's own.</param>
/// <param name="ClosedDay">The day a scene settled it, or null while it is open.</param>
public sealed record StoryThread(long Id, string? CharacterId, string Text, int OpenedDay, int? ClosedDay);

public static class StoryThreads
{
    /// <summary>The most loose ends a packet carries.</summary>
    public const int PerPacket = 5;

    /// <summary>A loose end nobody picked up for this many days is let go.</summary>
    public const int StaleAfterDays = 10;

    /// <summary>The most new loose ends one scene or reaction adds.</summary>
    public const int PerAnswer = 2;

    public const int MaxLength = 200;

    /// <summary>
    /// The loose ends for a scene: those about the people present first, newest first, then the player's own,
    /// leaving out any older than <see cref="StaleAfterDays"/>.
    /// </summary>
    public static IReadOnlyList<StoryThread> ForScene(IEnumerable<StoryThread> open, IReadOnlyCollection<string> presentIds, int day)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(presentIds);

        return
        [
            .. open
                .Where(t => t.ClosedDay is null && day - t.OpenedDay <= StaleAfterDays)
                .Where(t => t.CharacterId is null || presentIds.Contains(t.CharacterId))
                .OrderBy(t => t.CharacterId is null ? 1 : 0)
                .ThenByDescending(t => t.OpenedDay)
                .ThenByDescending(t => t.Id)
                .Take(PerPacket),
        ];
    }

    /// <summary>New loose ends worth keeping from an answer: trimmed, not blank, not too long, not repeated, at most <see cref="PerAnswer"/>.</summary>
    public static IReadOnlyList<string> Keep(IEnumerable<string>? proposed) =>
    [
        .. (proposed ?? [])
            .Select(t => t?.Trim() ?? "")
            .Where(t => t.Length is > 0 and <= MaxLength)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(PerAnswer),
    ];
}
