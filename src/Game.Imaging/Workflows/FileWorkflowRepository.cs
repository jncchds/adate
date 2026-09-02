using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Game.Imaging.Workflows;

/// <summary>
/// Loads manifests and API-format graphs from the <c>workflows/</c> directory.
/// Templates are cached in memory and never handed out directly; the patcher clones.
/// </summary>
public sealed class FileWorkflowRepository : IWorkflowRepository
{
    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, Task<LoadedWorkflow>> _cache = new(StringComparer.Ordinal);
    private readonly string _root;

    public FileWorkflowRepository(IOptions<WorkflowOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _root = Path.GetFullPath(options.Value.Directory);
    }

    public Task<LoadedWorkflow> GetAsync(string workflowId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);

        // GetOrAdd may invoke the factory more than once under contention; loading twice is
        // harmless and cheaper than holding a lock across file IO.
        return _cache.GetOrAdd(workflowId, id => LoadAsync(id, ct));
    }

    private async Task<LoadedWorkflow> LoadAsync(string workflowId, CancellationToken ct)
    {
        var manifestPath = Path.Combine(_root, $"{workflowId}.manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException(
                $"No workflow manifest for '{workflowId}'. Expected '{manifestPath}'. " +
                "Export the workflow from ComfyUI in API format and add its node id map.",
                manifestPath);
        }

        var manifestText = await File.ReadAllTextAsync(manifestPath, ct);
        var manifest = JsonSerializer.Deserialize<WorkflowManifest>(manifestText, ManifestJson)
            ?? throw new InvalidOperationException($"Workflow manifest '{manifestPath}' deserialised to null.");

        if (!string.Equals(manifest.Id, workflowId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Workflow manifest '{manifestPath}' declares id '{manifest.Id}' but was loaded as '{workflowId}'.");
        }

        var graphPath = Path.Combine(_root, manifest.WorkflowFile);
        if (!File.Exists(graphPath))
        {
            throw new FileNotFoundException(
                $"Workflow '{workflowId}' references graph '{manifest.WorkflowFile}', which is missing. " +
                "Note that ComfyUI's normal Save produces a UI-format graph; this needs the API format export.",
                graphPath);
        }

        var graphText = await File.ReadAllTextAsync(graphPath, ct);
        var graph = JsonNode.Parse(graphText) as JsonObject
            ?? throw new InvalidOperationException($"Workflow graph '{graphPath}' is not a JSON object.");

        Validate(manifest, graph, graphPath);

        // Both texts, because the node id map decides which graph inputs a request can
        // reach at all: a manifest edit changes the output just as a graph edit does.
        return new LoadedWorkflow(manifest, graph, Caching.ContentAddress.OfManifest(manifestText + graphText));
    }

    /// <summary>
    /// Fails at load time rather than at generation time. A manifest that points at a node
    /// the graph does not contain is the single most likely mistake when re-exporting a
    /// workflow, and it is much cheaper to catch here than after a 90-second queue wait.
    /// </summary>
    private static void Validate(WorkflowManifest manifest, JsonObject graph, string graphPath)
    {
        foreach (var (name, path) in manifest.Inputs)
        {
            var nodeId = path.Split('.', 2)[0];
            if (!graph.ContainsKey(nodeId))
            {
                throw new InvalidOperationException(
                    $"Workflow '{manifest.Id}': input '{name}' maps to node '{nodeId}', " +
                    $"which does not exist in '{graphPath}'.");
            }
        }

        foreach (var nodeId in manifest.OutputNodes)
        {
            if (!graph.ContainsKey(nodeId))
            {
                throw new InvalidOperationException(
                    $"Workflow '{manifest.Id}': declared output node '{nodeId}' does not exist in '{graphPath}'.");
            }
        }
    }
}

public sealed class WorkflowOptions
{
    public const string SectionName = "Workflows";

    /// <summary>Directory holding <c>*.manifest.json</c> and the API-format graphs.</summary>
    public string Directory { get; set; } = "workflows";
}
