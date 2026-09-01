using System.Text.Json.Nodes;

namespace Game.Imaging.Workflows;

/// <summary>Supplies workflow templates and their node id maps by logical id.</summary>
public interface IWorkflowRepository
{
    Task<(WorkflowManifest Manifest, JsonObject Graph)> GetAsync(string workflowId, CancellationToken ct = default);
}
