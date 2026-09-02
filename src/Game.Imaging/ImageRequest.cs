using Game.Core;

namespace Game.Imaging;

/// <summary>
/// A single generation job, fully specified. Every field participates in the
/// content-addressed cache key (HANDOFF 1.7), so two requests that differ in any way
/// address different files and two identical requests always hit cache.
/// </summary>
/// <param name="WorkflowId">Selects the workflow manifest: <c>portrait</c>, <c>sprite</c>, <c>background</c>.</param>
/// <param name="PackFingerprint">
/// Hash of the resolved style pack manifest. NOT in the original HANDOFF 4 sketch, but
/// required for correctness: identical prompt + seed on a different checkpoint, LoRA set
/// or sampler produces a different image, and without this the cache would serve the
/// wrong one.
/// </param>
/// <param name="PoseImageHash">
/// Content hash of the ControlNet pose skeleton, or null. Spike 0 showed the prompt
/// cannot hold pose and framing steady: with seed and anchor fixed, changing only the
/// mood tag still moved the body, and sprites whose bodies differ cannot crossfade.
/// A skeleton is authored once per pose slot and reused by every expression in that set.
/// </param>
/// <param name="PoseStrength">ControlNet strength. Null leaves the workflow default.</param>
/// <param name="AnchorImageHash">
/// Content hash of the IP-Adapter reference image, or null. HANDOFF 4 called this
/// <c>AnchorImagePath</c>, which assumes ComfyUI shares a filesystem with the game.
/// It does not: the services are independently addressable and routinely live on
/// different machines, so the anchor is uploaded to Comfy and referenced by hash here.
/// </param>
public sealed record ImageRequest(
    string WorkflowId,
    string Positive,
    string Negative,
    long Seed,
    int Width,
    int Height,
    string PackFingerprint,
    string? AnchorImageHash,
    double? AnchorWeight,
    string? PoseImageHash,
    double? PoseStrength,
    Ceiling Ceiling);
