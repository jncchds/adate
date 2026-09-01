using System.Text.Json.Nodes;

namespace Game.Imaging.Comfy;

/// <summary>
/// Transport-level access to a ComfyUI server. Knows the protocol; knows nothing about
/// characters, style packs or caching.
/// </summary>
public interface IComfyClient
{
    /// <summary>
    /// Submits a patched API-format graph and waits for it to finish, returning the images
    /// it produced. Combines HANDOFF 6 protocol steps 1-3.
    /// </summary>
    /// <param name="outputNodes">
    /// Restricts results to these node ids. Empty means accept images from any node, which
    /// is correct only while a graph has exactly one image output.
    /// </param>
    Task<IReadOnlyList<ComfyImageRef>> RunAsync(
        JsonObject graph,
        IReadOnlyList<string> outputNodes,
        IProgress<ComfyProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>Downloads an output image's bytes. HANDOFF 6 protocol step 4.</summary>
    Task<byte[]> ViewAsync(ComfyImageRef image, CancellationToken ct = default);

    /// <summary>
    /// Uploads an image into ComfyUI's input directory and returns the name a LoadImage
    /// node should reference. Required because the game and ComfyUI do not share a
    /// filesystem, so an IP-Adapter anchor cannot simply be passed as a local path.
    /// </summary>
    Task<UploadedImage> UploadImageAsync(
        string filename,
        ReadOnlyMemory<byte> content,
        CancellationToken ct = default);

    /// <summary>
    /// <c>POST /free</c>. Unused in Spike 0; required by <c>SwapGpuLease</c> later, which is
    /// why it exists now (HANDOFF 6).
    /// </summary>
    Task FreeAsync(bool unloadModels = true, bool freeMemory = true, CancellationToken ct = default);

    /// <summary>Reports VRAM headroom and server version. Used by the CLI preflight.</summary>
    Task<ComfySystemStats> GetSystemStatsAsync(CancellationToken ct = default);
}
