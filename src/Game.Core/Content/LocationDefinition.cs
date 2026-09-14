using Game.Core.Scenes;

namespace Game.Core.Content;

/// <summary>
/// A place the game can render. Locations are content, not code, for the same reason style
/// packs are: adding one should not mean a recompile.
/// </summary>
/// <param name="Tags">Base scene tags, emitted in this order. Order is load-bearing.</param>
/// <param name="TimeTags">
/// Extra tags per <see cref="TimeOfDay"/>, keyed by enum name. A cafe at night is not the
/// same prompt as a cafe at midday, and the difference is more than one lighting word.
/// </param>
/// <param name="Description">
/// The same place as a descriptive phrase, for natural-language checkpoints. Tags are booru
/// vocabulary and read badly as prose. Optional so a catalog used only by booru packs still
/// loads; the natural compiler refuses a location without one.
/// </param>
/// <param name="TimeDescriptions">The time-of-day lighting as phrases, keyed like <paramref name="TimeTags"/>.</param>
public sealed record LocationDefinition(
    string Id,
    string DisplayName,
    IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, IReadOnlyList<string>> TimeTags,
    string? Description = null,
    IReadOnlyDictionary<string, string>? TimeDescriptions = null);
