using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Game.Core.Gpu;
using Game.Imaging.Caching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Game.Imaging.ZImage;

/// <summary>
/// An <see cref="IImageProvider"/> backed by the Z-Image API: cache lookup, then GPU lease,
/// then <c>POST /generate</c> and <c>GET /images/{id}</c>, then persistence into the
/// content-addressed store.
/// </summary>
/// <remarks>
/// <para>
/// Prompt and seed only. The API has no pose or reference-image input, so a request carrying
/// either is refused rather than rendered without it. Silently dropping the skeleton would
/// store an unposed image under an address that says it was posed, and being content-addressed
/// it would never be regenerated.
/// </para>
/// <para>
/// Workflows listed in <see cref="ZImageOptions.MatteWorkflows"/> are matted on the server and
/// come back with alpha, so sprites can be composited over a separately generated background.
/// </para>
/// <para>
/// The API's own <c>/characters</c> store is deliberately unused. It only prepends a stored
/// description to the prompt, which the prompt compiler already does from the character record
/// the game owns.
/// </para>
/// </remarks>
public sealed class ZImageImageProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<ZImageOptions> options,
    IImageStore store,
    IGpuLease gpuLease,
    ILogger<ZImageImageProvider> log) : IImageProvider
{
    public const string HttpClientName = "zimage";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly ZImageOptions _options = options.Value;

    public async Task<GeneratedImage> GenerateAsync(ImageRequest req, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);

        if (req.PoseImageHash is not null || req.AnchorImageHash is not null)
        {
            throw new NotSupportedException(
                $"Z-Image cannot condition on a pose skeleton or an anchor image, but the " +
                $"'{req.WorkflowId}' request carries one. Rendering it without would cache an " +
                "image that does not match its own address. Use a pack whose workflows need " +
                "neither, or the ComfyUI provider.");
        }

        // Measured on Z-Image Turbo: at cfg 1.0 a negative renders byte-identical to none. Sending
        // one would change the cache key without changing the image, and let a pack believe its
        // negatives protect something. A pack for this provider declares negativePrompts: Ignored.
        if (!string.IsNullOrWhiteSpace(req.Negative) && _options.Cfg <= 1.0)
        {
            throw new NotSupportedException(
                $"The '{req.WorkflowId}' request carries a negative prompt, but Z-Image at cfg " +
                $"{_options.Cfg} never applies one. Use a pack that declares negativePrompts: Ignored.");
        }

        var hash = ContentAddress.For(req, Fingerprint(_options, req.WorkflowId));

        if (store.Exists(hash))
        {
            log.LogDebug("Cache hit for {WorkflowId} at {Hash}.", req.WorkflowId, hash);
            return new GeneratedImage(hash, store.RelativePath(hash), req.Width, req.Height, FromCache: true);
        }

        var stopwatch = Stopwatch.StartNew();

        // HANDOFF 1.6: every GPU call goes through a lease, even while the lease does nothing.
        await using var lease = await gpuLease.AcquireAsync(GpuConsumer.Image, ct).ConfigureAwait(false);

        var http = httpClientFactory.CreateClient(HttpClientName);

        var generated = await GenerateCoreAsync(http, req, ct).ConfigureAwait(false);
        var bytes = await DownloadAsync(http, generated.ImageId, ct).ConfigureAwait(false);

        await store.SaveAsync(hash, bytes, ct).ConfigureAwait(false);

        log.LogInformation(
            "Generated {WorkflowId} via Z-Image in {Elapsed:0.0}s ({Bytes} bytes) at {Hash}.",
            req.WorkflowId,
            stopwatch.Elapsed.TotalSeconds,
            bytes.Length,
            hash);

        return new GeneratedImage(hash, store.RelativePath(hash), req.Width, req.Height, FromCache: false);
    }

    /// <summary>Whether the server should cut the subject out of this workflow's render.</summary>
    internal static bool Mattes(ZImageOptions options, string workflowId) =>
        options.MatteWorkflows.Contains(workflowId, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Stands in for the workflow fingerprint the ComfyUI provider hashes into the address.
    /// There is no graph here, so the equivalent is every server-side generation parameter the
    /// request itself does not carry. The model is not included: the style pack names it, and
    /// the pack is already in the address.
    /// </summary>
    internal static string Fingerprint(ZImageOptions options, string workflowId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"zimage-api;steps={options.Steps};cfg={options.Cfg:R};matte={(Mattes(options, workflowId) ? options.MattingModel : "none")}");

    internal static GenerateRequest BuildRequest(ImageRequest req, ZImageOptions options) => new(
        Prompt: req.Positive,
        NegativePrompt: req.Negative,
        Seed: req.Seed,
        NumInferenceSteps: options.Steps,
        CfgScale: options.Cfg,
        Width: req.Width,
        Height: req.Height,
        Background: Mattes(options, req.WorkflowId) ? "remove" : "keep");

    private async Task<GenerateResponse> GenerateCoreAsync(HttpClient http, ImageRequest req, CancellationToken ct)
    {
        using var response = await http
            .PostAsJsonAsync("generate", BuildRequest(req, _options), Json, ct)
            .ConfigureAwait(false);

        await EnsureSuccessAsync(response, $"generate '{req.WorkflowId}'", ct).ConfigureAwait(false);

        var body = await response.Content.ReadFromJsonAsync<GenerateResponse>(Json, ct).ConfigureAwait(false);

        return body is { ImageId.Length: > 0 }
            ? body
            : throw new ZImageException($"Z-Image accepted '{req.WorkflowId}' but returned no image id.");
    }

    private static async Task<byte[]> DownloadAsync(HttpClient http, string imageId, CancellationToken ct)
    {
        using var response = await http
            .GetAsync($"images/{Uri.EscapeDataString(imageId)}", ct)
            .ConfigureAwait(false);

        await EnsureSuccessAsync(response, $"download image '{imageId}'", ct).ConfigureAwait(false);

        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

        // The store trusts whatever it is handed, and a cached file is permanent. An error page
        // or a truncated body saved here would be served as art for the life of the save.
        return IsPng(bytes)
            ? bytes
            : throw new ZImageException($"Z-Image returned {bytes.Length} bytes for image '{imageId}' that are not a PNG.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // FastAPI puts the useful part -- "Generation failed: Failed to find C compiler" -- in
        // the body, not the status line.
        var detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        throw new ZImageException(
            $"Z-Image failed to {what}: {(int)response.StatusCode} {response.ReasonPhrase}. {detail}".TrimEnd());
    }

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static bool IsPng(ReadOnlySpan<byte> bytes) =>
        bytes.Length > PngSignature.Length && bytes.StartsWith(PngSignature);

    internal sealed record GenerateRequest(
        string Prompt,
        string NegativePrompt,
        long Seed,
        int NumInferenceSteps,
        double CfgScale,
        int Width,
        int Height,
        string Background);

    internal sealed record GenerateResponse(string ImageId, long Seed);
}
