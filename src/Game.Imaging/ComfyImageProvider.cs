using System.Diagnostics;
using Game.Core.Gpu;
using Game.Imaging.Caching;
using Game.Imaging.Comfy;
using Game.Imaging.Workflows;
using Microsoft.Extensions.Logging;

namespace Game.Imaging;

/// <summary>
/// The production <see cref="IImageProvider"/>: cache lookup, then GPU lease, then a patched
/// ComfyUI run, then persistence into the content-addressed store.
/// </summary>
public sealed class ComfyImageProvider(
    IComfyClient comfy,
    IWorkflowRepository workflows,
    IImageStore store,
    IGpuLease gpuLease,
    ILogger<ComfyImageProvider> log) : IImageProvider
{
    public async Task<GeneratedImage> GenerateAsync(ImageRequest req, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);

        var hash = ContentAddress.For(req);

        if (store.Exists(hash))
        {
            log.LogDebug("Cache hit for {WorkflowId} at {Hash}.", req.WorkflowId, hash);
            return new GeneratedImage(hash, store.RelativePath(hash), req.Width, req.Height, FromCache: true);
        }

        var (manifest, graph) = await workflows.GetAsync(req.WorkflowId, ct).ConfigureAwait(false);

        // The anchor lives in our store, not on the ComfyUI host, so it has to be pushed
        // across before the graph can reference it. Uploaded under its own content hash,
        // which makes repeat uploads idempotent.
        string? anchorFilename = null;
        if (req.AnchorImageHash is { } anchorHash)
        {
            anchorFilename = await UploadAnchorAsync(anchorHash, ct).ConfigureAwait(false);
        }

        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [WorkflowInputs.Positive] = req.Positive,
            [WorkflowInputs.Negative] = req.Negative,
            [WorkflowInputs.Seed] = req.Seed,
            [WorkflowInputs.Width] = req.Width,
            [WorkflowInputs.Height] = req.Height,
            [WorkflowInputs.Anchor] = anchorFilename,
            [WorkflowInputs.AnchorWeight] = req.AnchorWeight,
        };

        var patched = WorkflowPatcher.Patch(graph, manifest, values);

        var stopwatch = Stopwatch.StartNew();

        // HANDOFF 1.6: every GPU call goes through a lease, even while the lease does nothing.
        await using var lease = await gpuLease.AcquireAsync(GpuConsumer.Image, ct).ConfigureAwait(false);

        var outputs = await comfy.RunAsync(patched, manifest.OutputNodes, progress: null, ct).ConfigureAwait(false);

        if (outputs.Count > 1)
        {
            // Ambiguity here means the wrong image could be cached under this hash, and being
            // content-addressed it would never be regenerated. Better to fail than to guess.
            throw new ComfyException(
                $"Workflow '{req.WorkflowId}' returned {outputs.Count} images for a single request. " +
                "Declare OutputNodes in the manifest so exactly one image is selected.");
        }

        var bytes = await comfy.ViewAsync(outputs[0], ct).ConfigureAwait(false);
        await store.SaveAsync(hash, bytes, ct).ConfigureAwait(false);

        log.LogInformation(
            "Generated {WorkflowId} in {Elapsed:0.0}s ({Bytes} bytes) at {Hash}.",
            req.WorkflowId,
            stopwatch.Elapsed.TotalSeconds,
            bytes.Length,
            hash);

        return new GeneratedImage(hash, store.RelativePath(hash), req.Width, req.Height, FromCache: false);
    }

    private async Task<string> UploadAnchorAsync(string anchorHash, CancellationToken ct)
    {
        if (!store.Exists(anchorHash))
        {
            throw new InvalidOperationException(
                $"Anchor image '{anchorHash}' is not in the image store. " +
                "A sprite cannot be generated before its character's anchor portrait has been approved.");
        }

        var anchorBytes = await store.ReadAsync(anchorHash, ct).ConfigureAwait(false);
        var uploaded = await comfy.UploadImageAsync($"{anchorHash}.png", anchorBytes, ct).ConfigureAwait(false);

        // ComfyUI may return a different name than requested if it declined to overwrite.
        return string.IsNullOrEmpty(uploaded.Subfolder)
            ? uploaded.Name
            : $"{uploaded.Subfolder}/{uploaded.Name}";
    }
}
