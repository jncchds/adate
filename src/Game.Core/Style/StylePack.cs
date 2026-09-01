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
    /// Extra negative tokens applied per ceiling. Keyed by <see cref="Ceiling"/> name.
    /// A PG13 save adds its nudity/suggestive negatives from here.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> NegativeByCeiling { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>();

    public bool Supports(Ceiling ceiling) => SupportedCeilings.Contains(ceiling);
}

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
