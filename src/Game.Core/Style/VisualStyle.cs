using System.Text.RegularExpressions;

namespace Game.Core.Style;

/// <summary>A named look offered at new game: the style sentence that leads every picture prompt.</summary>
/// <param name="Id">What the save stores.</param>
/// <param name="Label">What the player sees and may type.</param>
/// <param name="Prefix">The pack's style prefix is replaced by this; empty keeps the pack's own.</param>
public sealed record VisualStylePreset(string Id, string Label, IReadOnlyList<string> Prefix);

/// <summary>
/// How a save's pictures look. The player picks a preset or types their own style when starting a game;
/// it replaces the style sentence of a natural-language pack and nothing else, so subjects, age bands,
/// outfits and the content rules stay the pack's. Locked per save like the pack: a character drawn in
/// one style is a different picture in another.
/// </summary>
/// <remarks>
/// Only <see cref="PromptDialect.Natural"/> packs take it. The presets are prose for Z-Image's text
/// encoder, and a sentence read as booru tags is noise.
/// </remarks>
public static partial class VisualStyle
{
    public const string Default = "anime";

    public const int MaxLength = 80;

    /// <summary>
    /// The offered looks. Anime keeps the pack's own wording, so saves from before styles keep their
    /// pictures and cache. No 3D and no named webtoon style: glossy 3D gave adults childlike proportions,
    /// and "Korean webtoon" changed the declared ethnicity (docs/decisions.md).
    /// </summary>
    public static IReadOnlyList<VisualStylePreset> Presets { get; } =
    [
        new(Default, "Anime", []),
        new("realistic", "Realistic", ["A photorealistic photograph", "natural soft lighting", "shallow depth of field", "high quality"]),
        new("semi-realistic", "Semi-realistic painting", ["A semi-realistic digital painting", "soft painterly brushwork", "warm cinematic lighting", "high quality"]),
        new("watercolor", "Watercolour", ["A soft watercolor illustration", "delicate washes of color on textured paper", "gentle warm colors", "high quality"]),
        new("storybook", "Storybook", ["A gentle storybook illustration", "soft gouache textures", "muted pastel colors", "high quality"]),
        new("comic", "Comic book", ["A comic book illustration", "bold ink outlines", "flat vibrant colors", "high quality"]),
        new("film", "Cinematic film still", ["A cinematic film still", "35mm film grain", "moody warm lighting", "high quality"]),
    ];

    /// <summary>
    /// A preset's id when the typed text names one by id or label; otherwise the typed style, trimmed and
    /// with spaces collapsed. Anime when left blank.
    /// </summary>
    /// <param name="pack">When given, a typed style must also pass the pack's content rules at <paramref name="ceiling"/>.</param>
    public static string Normalize(string? typed, StylePack? pack = null, Ceiling ceiling = Ceiling.PG13)
    {
        var collapsed = Spaces().Replace(typed?.Trim() ?? "", " ");
        if (collapsed.Length == 0)
        {
            return Default;
        }

        if (Find(collapsed) is { } preset)
        {
            return preset.Id;
        }

        if (collapsed.Length > MaxLength || !Words().IsMatch(collapsed))
        {
            throw new ArgumentException($"Describe the visual style in words, such as watercolour or oil painting, up to {MaxLength} characters.");
        }

        if (pack is not null && Phrases(collapsed).Any(p => !pack.PermitsPositive(p, ceiling)))
        {
            throw new ArgumentException("That visual style names something the content rules do not allow.");
        }

        return collapsed;
    }

    /// <summary>The label to show for a stored style: a preset's label, or the typed style as it is.</summary>
    public static string LabelFor(string? style) =>
        string.IsNullOrWhiteSpace(style) ? Presets[0].Label : Find(style)?.Label ?? style.Trim();

    /// <summary>
    /// <paramref name="pack"/> drawn in <paramref name="style"/>: the style prefix replaced for a natural-language
    /// pack, unchanged for anime, no style (saves from before styles) or a tag pack. A typed phrase the pack's
    /// content rules refuse at <paramref name="ceiling"/> is dropped, as the gate drops story-written terms.
    /// </summary>
    public static StylePack Apply(StylePack pack, string? style, Ceiling ceiling)
    {
        ArgumentNullException.ThrowIfNull(pack);

        if (string.IsNullOrWhiteSpace(style) || pack.Dialect is not PromptDialect.Natural)
        {
            return pack;
        }

        IReadOnlyList<string> prefix = Find(style) is { } preset
            ? preset.Prefix
            : [.. Phrases(style).Where(p => pack.PermitsPositive(p, ceiling)), "high quality"];

        return prefix.Count == 0 ? pack : pack with { PositivePrefix = prefix };
    }

    private static VisualStylePreset? Find(string style)
    {
        var trimmed = style.Trim();
        return Presets.FirstOrDefault(p =>
            string.Equals(p.Id, trimmed, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.Label, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> Phrases(string style) =>
        style.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^[\p{L}\p{M}\p{N}][\p{L}\p{M}\p{N} ,.'&()\-]*$")]
    private static partial Regex Words();
}
