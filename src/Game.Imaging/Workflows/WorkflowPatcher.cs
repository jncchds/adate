using System.Globalization;
using System.Text.Json.Nodes;

namespace Game.Imaging.Workflows;

/// <summary>
/// Applies logical input values onto a copy of an API-format graph, using the node paths
/// declared in a <see cref="WorkflowManifest"/>. Never mutates the loaded template.
/// </summary>
public static class WorkflowPatcher
{
    /// <summary>
    /// Returns a patched clone of <paramref name="graph"/>.
    /// </summary>
    /// <param name="values">
    /// Logical input name to value. A name absent from the manifest's map is ignored,
    /// which is how one patcher serves graphs that differ in capability (a background
    /// graph has no anchor). A name present in the map but missing a target node is an
    /// error, because that means the manifest and the graph have drifted apart.
    /// </param>
    public static JsonObject Patch(
        JsonObject graph,
        WorkflowManifest manifest,
        IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(values);

        var clone = graph.DeepClone().AsObject();

        foreach (var (name, value) in values)
        {
            if (value is null)
            {
                continue;
            }

            if (!manifest.Inputs.TryGetValue(name, out var path))
            {
                // The graph has no slot for this input. Legitimate: not every workflow
                // takes an anchor or an explicit sampler override.
                continue;
            }

            SetByPath(clone, path, value, manifest.Id);
        }

        return clone;
    }

    /// <summary>
    /// Resolves a dotted path such as <c>6.inputs.text</c> and assigns a value.
    /// The final segment is the property to set; every earlier segment must already exist.
    /// </summary>
    private static void SetByPath(JsonObject graph, string path, object value, string manifestId)
    {
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            throw new WorkflowPatchException(
                $"Workflow '{manifestId}': input path '{path}' must have at least a node id and a property.");
        }

        JsonNode? cursor = graph;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (cursor is not JsonObject obj || !obj.TryGetPropertyValue(segments[i], out cursor) || cursor is null)
            {
                throw new WorkflowPatchException(
                    $"Workflow '{manifestId}': path '{path}' broke at segment '{segments[i]}'. " +
                    "The graph and its manifest have drifted; re-export the workflow or fix the node id map.");
            }
        }

        if (cursor is not JsonObject target)
        {
            throw new WorkflowPatchException(
                $"Workflow '{manifestId}': path '{path}' does not address a JSON object.");
        }

        target[segments[^1]] = ToJson(value);
    }

    private static JsonNode ToJson(object value) => value switch
    {
        string s => JsonValue.Create(s),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        float f => JsonValue.Create((double)f),
        bool b => JsonValue.Create(b),
        JsonNode n => n.DeepClone(),
        _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
    };
}

public sealed class WorkflowPatchException(string message) : Exception(message);
