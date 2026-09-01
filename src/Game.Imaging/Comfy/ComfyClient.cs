using System.Buffers;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Game.Imaging.Comfy;

/// <summary>
/// ComfyUI HTTP + websocket client, implementing the protocol in HANDOFF 6.
/// </summary>
/// <remarks>
/// <para>
/// The websocket is opened <em>before</em> the prompt is queued. ComfyUI routes execution
/// messages to the socket registered under the same <c>clientId</c>, and a fast graph can
/// finish before a socket opened afterwards would have attached, losing the completion
/// message entirely.
/// </para>
/// <para>
/// A socket is opened per run rather than kept alive. Generation is bursty — most turns hit
/// the image cache and generate nothing — so a persistent socket would spend nearly all of
/// its life idle while needing reconnect and resubscribe logic to earn its keep.
/// </para>
/// <para>
/// The websocket is an optimisation, never the source of truth. If it drops, closes, or the
/// server restarts, the run falls back to polling <c>/history</c>, which is authoritative
/// about what a prompt actually produced.
/// </para>
/// </remarks>
public sealed class ComfyClient : IComfyClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ComfyOptions _options;
    private readonly ILogger<ComfyClient> _log;

    public ComfyClient(HttpClient http, IOptions<ComfyOptions> options, ILogger<ComfyClient> log)
    {
        ArgumentNullException.ThrowIfNull(options);
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options.Value;
        _log = log ?? throw new ArgumentNullException(nameof(log));

        // A base address whose path does not end in a slash makes HttpClient drop the last
        // path segment when resolving a relative URI, which silently breaks ComfyUI hosted
        // under a reverse-proxy subpath.
        _http.BaseAddress = EnsureTrailingSlash(_http.BaseAddress ?? _options.BaseAddress);
    }

    internal static Uri EnsureTrailingSlash(Uri uri) =>
        uri.AbsolutePath.EndsWith('/')
            ? uri
            : new UriBuilder(uri) { Path = uri.AbsolutePath + "/" }.Uri;

    public async Task<IReadOnlyList<ComfyImageRef>> RunAsync(
        JsonObject graph,
        IReadOnlyList<string> outputNodes,
        IProgress<ComfyProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(outputNodes);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.GenerationTimeout);
        var token = timeout.Token;

        try
        {
            return await RunCoreAsync(graph, outputNodes, progress, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The linked source fired, not the caller's token: this is our own deadline.
            throw new ComfyException(
                $"ComfyUI did not finish the prompt within {_options.GenerationTimeout}. " +
                "A cold model load or a busy queue can legitimately exceed a short timeout; " +
                $"raise {nameof(ComfyOptions)}.{nameof(ComfyOptions.GenerationTimeout)} if this is expected.");
        }
    }

    private async Task<IReadOnlyList<ComfyImageRef>> RunCoreAsync(
        JsonObject graph,
        IReadOnlyList<string> outputNodes,
        IProgress<ComfyProgress>? progress,
        CancellationToken token)
    {
        var clientId = Guid.NewGuid().ToString("D");

        using var socket = new ClientWebSocket();
        var socketOpen = await TryConnectAsync(socket, clientId, token).ConfigureAwait(false);

        var queued = await QueueAsync(graph, clientId, token).ConfigureAwait(false);
        _log.LogDebug("Queued prompt {PromptId} at queue position {Number}.", queued.PromptId, queued.Number);

        if (socketOpen)
        {
            try
            {
                await AwaitCompletionAsync(socket, queued.PromptId, progress, token).ConfigureAwait(false);
            }
            catch (ComfyExecutionException)
            {
                // A node genuinely failed. Polling /history would only rediscover the same
                // failure, so surface it now with the traceback intact.
                throw;
            }
            catch (Exception ex) when (ex is WebSocketException or InvalidOperationException)
            {
                _log.LogWarning(
                    ex,
                    "Websocket failed while awaiting {PromptId}; falling back to history polling.",
                    queued.PromptId);
                await PollHistoryAsync(queued.PromptId, token).ConfigureAwait(false);
            }
        }
        else
        {
            await PollHistoryAsync(queued.PromptId, token).ConfigureAwait(false);
        }

        return await ReadOutputsAsync(queued.PromptId, outputNodes, token).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ queue

    private async Task<QueuedPrompt> QueueAsync(JsonObject graph, string clientId, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["prompt"] = graph.DeepClone(),
            ["client_id"] = clientId,
        };

        using var response = await _http.PostAsJsonAsync("prompt", body, Json, ct).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // ComfyUI answers a rejected graph with 400 and a body naming the offending node
            // and input. That body is the entire diagnostic, so it must not be discarded.
            throw new ComfyException(
                $"ComfyUI rejected the graph ({(int)response.StatusCode} {response.ReasonPhrase}): {Truncate(payload, 4000)}");
        }

        if (JsonNode.Parse(payload) is not JsonObject node)
        {
            throw new ComfyException($"Unexpected /prompt response: {Truncate(payload, 500)}");
        }

        if (node["node_errors"] is JsonObject errors && errors.Count > 0)
        {
            throw new ComfyException($"ComfyUI reported node errors: {errors.ToJsonString()}");
        }

        var promptId = node["prompt_id"]?.GetValue<string>()
            ?? throw new ComfyException($"/prompt response carried no prompt_id: {Truncate(payload, 500)}");

        return new QueuedPrompt(promptId, node["number"]?.GetValue<int>() ?? 0);
    }

    // -------------------------------------------------------------- websocket

    private async Task<bool> TryConnectAsync(ClientWebSocket socket, string clientId, CancellationToken ct)
    {
        var wsUri = BuildWebSocketUri(_options.BaseAddress, clientId);
        try
        {
            await socket.ConnectAsync(wsUri, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException)
        {
            // Not fatal: history polling covers it, at the cost of latency and progress.
            _log.LogWarning(ex, "Could not open ComfyUI websocket at {Uri}; will poll /history instead.", wsUri);
            return false;
        }
    }

    internal static Uri BuildWebSocketUri(Uri baseAddress, string clientId)
    {
        var builder = new UriBuilder(baseAddress)
        {
            Scheme = baseAddress.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
            Query = $"clientId={Uri.EscapeDataString(clientId)}",
        };

        // Preserve any base path, so ComfyUI behind a reverse-proxy subpath still resolves.
        builder.Path = builder.Path.TrimEnd('/') + "/ws";
        return builder.Uri;
    }

    /// <summary>
    /// Reads execution messages until this prompt reports completion. Completion is an
    /// <c>executing</c> message carrying a null <c>node</c> for our prompt id (HANDOFF 6).
    /// </summary>
    private async Task AwaitCompletionAsync(
        ClientWebSocket socket,
        string promptId,
        IProgress<ComfyProgress>? progress,
        CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            var message = new ArrayBufferWriter<byte>(16 * 1024);

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                message.Clear();
                var isText = await ReceiveMessageAsync(socket, buffer, message, ct).ConfigureAwait(false);

                if (isText is null)
                {
                    throw new InvalidOperationException(
                        $"ComfyUI closed the websocket before prompt {promptId} completed.");
                }

                // Binary frames are live preview images. Nothing here consumes them.
                if (isText == false)
                {
                    continue;
                }

                if (HandleMessage(message.WrittenSpan, promptId, progress))
                {
                    return;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads one complete websocket message, which may span several frames.
    /// Returns true for text, false for binary, null if the socket closed.
    /// </summary>
    private static async Task<bool?> ReceiveMessageAsync(
        ClientWebSocket socket,
        byte[] buffer,
        ArrayBufferWriter<byte> into,
        CancellationToken ct)
    {
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            into.Write(buffer.AsSpan(0, result.Count));
        }
        while (!result.EndOfMessage);

        return result.MessageType == WebSocketMessageType.Text;
    }

    /// <summary>Returns true when the message means "this prompt is finished".</summary>
    internal bool HandleMessage(ReadOnlySpan<byte> utf8, string promptId, IProgress<ComfyProgress>? progress)
    {
        JsonObject? envelope;
        try
        {
            envelope = JsonNode.Parse(utf8.ToArray()) as JsonObject;
        }
        catch (JsonException ex)
        {
            _log.LogDebug(ex, "Ignoring unparseable websocket frame.");
            return false;
        }

        if (envelope?["type"]?.GetValue<string>() is not { } type || envelope["data"] is not JsonObject data)
        {
            return false;
        }

        // Status frames are broadcast without a prompt id. Everything we act on carries one,
        // and frames belonging to another client's prompt must be ignored.
        var framePromptId = data["prompt_id"]?.GetValue<string>();
        if (framePromptId is not null && !string.Equals(framePromptId, promptId, StringComparison.Ordinal))
        {
            return false;
        }

        switch (type)
        {
            case "executing":
                // A null node against our prompt id is the documented completion signal.
                return framePromptId is not null && data["node"] is null;

            case "execution_success":
                return framePromptId is not null;

            case "progress":
                progress?.Report(new ComfyProgress(
                    promptId,
                    data["node"]?.GetValue<string>(),
                    data["value"]?.GetValue<int>() ?? 0,
                    data["max"]?.GetValue<int>() ?? 0));
                return false;

            case "execution_error":
                throw new ComfyExecutionException(
                    BuildErrorMessage(data),
                    promptId,
                    data["node_id"]?.GetValue<string>(),
                    data["node_type"]?.GetValue<string>(),
                    ReadTraceback(data));

            case "execution_interrupted":
                throw new ComfyExecutionException(
                    $"Prompt {promptId} was interrupted by the ComfyUI server.",
                    promptId,
                    data["node_id"]?.GetValue<string>(),
                    data["node_type"]?.GetValue<string>(),
                    []);

            default:
                return false;
        }
    }

    private static string BuildErrorMessage(JsonObject data)
    {
        var node = data["node_type"]?.GetValue<string>() ?? data["node_id"]?.GetValue<string>() ?? "unknown node";
        var message = data["exception_message"]?.GetValue<string>() ?? "no message";
        var type = data["exception_type"]?.GetValue<string>();

        return type is null
            ? $"ComfyUI node '{node}' failed: {message}"
            : $"ComfyUI node '{node}' failed with {type}: {message}";
    }

    private static IReadOnlyList<string> ReadTraceback(JsonObject data) =>
        data["traceback"] is JsonArray lines
            ? [.. lines.Select(static l => l?.GetValue<string>() ?? string.Empty)]
            : [];

    // ---------------------------------------------------------------- history

    /// <summary>
    /// Waits for the prompt to appear in <c>/history</c>. Only used when the websocket was
    /// unavailable or died mid-run.
    /// </summary>
    private async Task PollHistoryAsync(string promptId, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (await TryGetHistoryAsync(promptId, ct).ConfigureAwait(false) is not null)
            {
                return;
            }

            await Task.Delay(_options.HistoryPollInterval, ct).ConfigureAwait(false);
        }
    }

    private async Task<JsonObject?> TryGetHistoryAsync(string promptId, CancellationToken ct)
    {
        using var response = await _http
            .GetAsync($"history/{Uri.EscapeDataString(promptId)}", ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (JsonNode.Parse(text) is not JsonObject root)
        {
            return null;
        }

        // Keyed by prompt id, and absent entirely until the prompt leaves the queue.
        return root[promptId] as JsonObject;
    }

    /// <summary>
    /// Reads the authoritative output list from <c>/history</c>. Preferred over collecting
    /// <c>executed</c> websocket frames, because history also reflects nodes whose results
    /// came from ComfyUI's own execution cache and therefore emitted no frame at all.
    /// </summary>
    private static IReadOnlyList<ComfyImageRef> ExtractImages(
        JsonObject entry,
        string promptId,
        IReadOnlyList<string> outputNodes)
    {
        if (entry["status"] is JsonObject status && status["status_str"]?.GetValue<string>() is "error")
        {
            throw new ComfyException($"Prompt {promptId} finished with status 'error': {status.ToJsonString()}");
        }

        if (entry["outputs"] is not JsonObject outputs)
        {
            throw new ComfyException($"Prompt {promptId} produced no outputs section.");
        }

        var images = new List<ComfyImageRef>();
        foreach (var (nodeId, nodeOutput) in outputs)
        {
            if (outputNodes.Count > 0 && !outputNodes.Contains(nodeId, StringComparer.Ordinal))
            {
                continue;
            }

            if (nodeOutput is not JsonObject node || node["images"] is not JsonArray nodeImages)
            {
                continue;
            }

            foreach (var image in nodeImages)
            {
                if (image is not JsonObject img || img["filename"]?.GetValue<string>() is not { } filename)
                {
                    continue;
                }

                images.Add(new ComfyImageRef(
                    filename,
                    img["subfolder"]?.GetValue<string>() ?? string.Empty,
                    img["type"]?.GetValue<string>() ?? "output"));
            }
        }

        if (images.Count == 0)
        {
            var expected = outputNodes.Count > 0 ? string.Join(", ", outputNodes) : "any node";
            throw new ComfyException(
                $"Prompt {promptId} completed but produced no images from {expected}. " +
                "The graph most likely has no SaveImage node, or the manifest's output nodes are wrong.");
        }

        return images;
    }

    private async Task<IReadOnlyList<ComfyImageRef>> ReadOutputsAsync(
        string promptId,
        IReadOnlyList<string> outputNodes,
        CancellationToken ct)
    {
        var entry = await TryGetHistoryAsync(promptId, ct).ConfigureAwait(false)
            ?? throw new ComfyException($"Prompt {promptId} completed but has no /history entry.");

        return ExtractImages(entry, promptId, outputNodes);
    }

    // ------------------------------------------------------------ view/upload

    public async Task<byte[]> ViewAsync(ComfyImageRef image, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);

        var url =
            $"view?filename={Uri.EscapeDataString(image.Filename)}" +
            $"&subfolder={Uri.EscapeDataString(image.Subfolder)}" +
            $"&type={Uri.EscapeDataString(image.Type)}";

        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new ComfyException(
                $"Could not fetch '{image.Filename}' from ComfyUI ({(int)response.StatusCode} {response.ReasonPhrase}).");
        }

        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    public async Task<UploadedImage> UploadImageAsync(
        string filename,
        ReadOnlyMemory<byte> content,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);

        using var form = new MultipartFormDataContent();
        var file = new ReadOnlyMemoryContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "image", filename);

        // Anchors are content-addressed, so the same name always carries the same bytes.
        // Overwriting stops ComfyUI's input directory accumulating "name (1).png" duplicates.
        form.Add(new StringContent("true"), "overwrite");
        form.Add(new StringContent("input"), "type");

        using var response = await _http.PostAsync("upload/image", form, ct).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new ComfyException(
                $"Anchor upload failed ({(int)response.StatusCode} {response.ReasonPhrase}): {Truncate(payload, 1000)}");
        }

        return JsonSerializer.Deserialize<UploadedImage>(payload, Json)
            ?? throw new ComfyException($"Unexpected /upload/image response: {Truncate(payload, 500)}");
    }

    // ----------------------------------------------------------- housekeeping

    public async Task FreeAsync(bool unloadModels = true, bool freeMemory = true, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["unload_models"] = unloadModels,
            ["free_memory"] = freeMemory,
        };

        using var response = await _http.PostAsJsonAsync("free", body, Json, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new ComfyException($"POST /free failed ({(int)response.StatusCode} {response.ReasonPhrase}).");
        }
    }

    public async Task<ComfySystemStats> GetSystemStatsAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("system_stats", ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new ComfyException(
                $"GET /system_stats failed ({(int)response.StatusCode} {response.ReasonPhrase}). " +
                $"Is ComfyUI reachable at {_options.BaseAddress}?");
        }

        var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<ComfySystemStats>(payload, Json)
            ?? throw new ComfyException("Unexpected /system_stats response.");
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : string.Concat(value.AsSpan(0, max), "...");
}
