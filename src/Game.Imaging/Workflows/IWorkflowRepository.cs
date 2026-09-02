namespace Game.Imaging.Workflows;

/// <summary>Supplies workflow templates, their node id maps and their fingerprints.</summary>
public interface IWorkflowRepository
{
    Task<LoadedWorkflow> GetAsync(string workflowId, CancellationToken ct = default);
}
