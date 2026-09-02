using System.Text.Json.Nodes;

namespace Game.Imaging.Workflows;

/// <summary>
/// A workflow template ready to patch, with the fingerprint that binds generated art to
/// the graph that produced it.
/// </summary>
/// <param name="Fingerprint">
/// Hash of the manifest and graph text. The graph is as much a generation parameter as the
/// prompt is -- rewiring a sampler or swapping a matting node changes the output for an
/// otherwise identical request -- so it belongs in the content address. Without it, editing
/// a workflow would leave every previously generated image cached under a key that no longer
/// describes it, and being content-addressed it would never be regenerated.
/// </param>
public sealed record LoadedWorkflow(WorkflowManifest Manifest, JsonObject Graph, string Fingerprint);
