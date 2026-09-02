using System.Text.Json.Serialization;

namespace Game.Core.Style;

/// <summary>
/// HANDOFF 1.5: a style pack is data, not code. Everything that decides how art looks
/// lives in a JSON manifest so packs can be added without recompiling.
/// A pack is locked per save; switching checkpoints destroys character consistency.
/// </summary>
public sealed record StylePack
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>Checkpoint filename as ComfyUI sees it, e.g. <c>meinamix_v12Final.safetensors</c>.</summary>
    public required string Checkpoint { get; init; }

    /// <summary>Base architecture. Decides latent size, IP-Adapter variant and VRAM budget.</summary>
    public required string BaseModel { get; init; }

    public required PromptDialect Dialect { get; init; }

    public required ConsistencyStrategy Consistency { get; init; }

    public IReadOnlyList<LoraSpec> Loras { get; init; } = [];

    public required SamplerSettings Sampler { get; init; }

    public required ResolutionSet Resolutions { get; init; }

    /// <summary>
    /// Background removal model used inside the Comfy graph (HANDOFF 6). Matting happens
    /// server-side in the graph so the client only ever receives PNGs with alpha.
    /// </summary>
    public required string MattingModel { get; init; }

    /// <summary>Ceilings this pack is willing to render. A save may not exceed these.</summary>
    public required IReadOnlyList<Ceiling> SupportedCeilings { get; init; }

    /// <summary>
    /// Tokens prepended to every positive prompt (quality boilerplate, e.g. Pony's
    /// <c>score_9</c> chain). Emitted before character tokens, in this order.
    /// </summary>
    public IReadOnlyList<string> PositivePrefix { get; init; } = [];

    /// <summary>Base negative tokens, before any ceiling-specific additions.</summary>
    public IReadOnlyList<string> NegativeBase { get; init; } = [];

    /// <summary>
    /// Subject anchors, keyed by <see cref="Characters.CharacterAppearance.Subject"/>. A game
    /// declares which subjects its love interests may use; the pack supplies the vocabulary
    /// each one needs, because the right tokens are checkpoint-specific and not something
    /// the compiler can know.
    /// </summary>
    /// <remarks>
    /// Measured on Illustrious XL: <c>1boy, solo, adult</c> alone renders androgynous and
    /// young, and only reads as an adult man once <c>male focus, mature male</c> is present
    /// with <c>1girl, feminine</c> negated. That is the male half of the HANDOFF 1.9 age
    /// anchor, and it is pack data for the same reason the female half's wording is.
    /// </remarks>
    public IReadOnlyDictionary<string, SubjectProfile> Subjects { get; init; }
        = new Dictionary<string, SubjectProfile>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The anchor for <paramref name="subject"/>, or a throw naming what the pack does offer.
    /// A missing subject is a content error, not a reason to silently fall back to another
    /// one: rendering a male love interest as a woman is worse than failing.
    /// </summary>
    public SubjectProfile SubjectFor(string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        // Deserialisation replaces the dictionary with a case-sensitive one, so the match is
        // done here rather than relying on the comparer. Subject keys come from saved
        // character data and pack JSON authored by different hands; casing is not a contract.
        foreach (var (key, profile) in Subjects)
        {
            if (string.Equals(key, subject, StringComparison.OrdinalIgnoreCase))
            {
                return profile;
            }
        }

        var known = Subjects.Count == 0 ? "none" : string.Join(", ", Subjects.Keys.Order(StringComparer.Ordinal));
        throw new InvalidOperationException(
            $"Style pack '{Id}' defines no subject '{subject}'. Known subjects: {known}.");
    }

    /// <summary>
    /// Extra negative tokens applied per ceiling. Keyed by <see cref="Ceiling"/> name.
    /// A PG13 save adds its nudity/suggestive negatives from here.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> NegativeByCeiling { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>();

    public bool Supports(Ceiling ceiling) => SupportedCeilings.Contains(ceiling);
}

/// <summary>
/// The tokens that establish who the subject of an image is.
/// </summary>
/// <param name="Positive">
/// Emitted immediately after the quality prefix and before the age tag, in this order.
/// Position is load-bearing: tag checkpoints weight early tokens most, and HANDOFF 1.9
/// requires the age anchor to precede every body descriptor.
/// </param>
/// <param name="Negative">
/// Added to the negative prompt for character renders only. Backgrounds negate every
/// subject regardless, so applying these there would be redundant.
/// </param>
public sealed record SubjectProfile(
    IReadOnlyList<string> Positive,
    IReadOnlyList<string> Negative);

public sealed record LoraSpec(string File, double ModelWeight, double ClipWeight);

public sealed record SamplerSettings(
    string SamplerName,
    string Scheduler,
    int Steps,
    double Cfg,
    double AnchorWeight);

/// <summary>
/// Native generation resolutions per job. Off-native sizes cost quality on SD1.5 and
/// coherence on SDXL, so these are pack data rather than call-site choices.
/// </summary>
public sealed record ResolutionSet(
    Size Portrait,
    Size Sprite,
    Size Background);

public readonly record struct Size(int Width, int Height);
