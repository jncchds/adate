namespace Game.Imaging.Comfy;

/// <summary>Anything ComfyUI refused or failed to do.</summary>
public class ComfyException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The graph was rejected or a node threw. Carries the node id and the Python traceback,
/// because a Comfy failure is almost always a missing model file or a missing custom node
/// and the traceback says which.
/// </summary>
public sealed class ComfyExecutionException(
    string message,
    string promptId,
    string? nodeId,
    string? nodeType,
    IReadOnlyList<string> traceback)
    : ComfyException(message)
{
    public string PromptId { get; } = promptId;

    public string? NodeId { get; } = nodeId;

    public string? NodeType { get; } = nodeType;

    public IReadOnlyList<string> Traceback { get; } = traceback;
}
