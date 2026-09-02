using System.Text.Json.Serialization;

namespace Game.Imaging.Workflows;

/// <summary>
/// HANDOFF 6: pairs an API-format ComfyUI graph with a map from logical input names to
/// node paths, so C# never hardcodes graph structure. Re-wiring a workflow in the ComfyUI
/// UI then means re-exporting the graph and editing this map, not changing code.
/// </summary>
public sealed record WorkflowManifest
{
    /// <summary>Logical id used by <see cref="ImageRequest.WorkflowId"/>: portrait | sprite | background.</summary>
    public required string Id { get; init; }

    /// <summary>API-format graph filename, resolved relative to the manifest.</summary>
    public required string WorkflowFile { get; init; }

    /// <summary>
    /// Logical input name to node path, e.g. <c>"positive": "6.inputs.text"</c>.
    /// Names are drawn from <see cref="WorkflowInputs"/>.
    /// </summary>
    public required IReadOnlyDictionary<string, string> Inputs { get; init; }

    /// <summary>
    /// Node ids whose outputs are the images we want. Left empty means "any node that
    /// produced images", which is right for single-output graphs and wrong the moment a
    /// graph also saves a debug preview.
    /// </summary>
    public IReadOnlyList<string> OutputNodes { get; init; } = [];
}

/// <summary>Well-known logical input names. Keeps manifest keys from drifting into typos.</summary>
public static class WorkflowInputs
{
    public const string Positive = "positive";
    public const string Negative = "negative";
    public const string Seed = "seed";
    public const string Width = "width";
    public const string Height = "height";
    public const string Checkpoint = "checkpoint";

    /// <summary>Filename of the uploaded anchor image, as a LoadImage node expects it.</summary>
    public const string Anchor = "anchor";

    public const string AnchorWeight = "anchorWeight";

    /// <summary>Filename of the uploaded ControlNet pose skeleton.</summary>
    public const string PoseImage = "poseImage";

    public const string PoseStrength = "poseStrength";
    public const string Steps = "steps";
    public const string Cfg = "cfg";
    public const string SamplerName = "samplerName";
    public const string Scheduler = "scheduler";
}
