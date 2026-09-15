using System.Text;
using System.Text.Json.Nodes;
using Game.Core.Gpu;
using Microsoft.Extensions.Options;

namespace Game.Llm.Providers;

/// <summary>
/// Ollama's native <c>POST api/chat</c>: the schema goes in <c>format</c>, which Ollama constrains decoding to,
/// and a reasoning effort of <c>none</c> turns thinking off. Every call holds the GPU lease (HANDOFF 1.6).
/// </summary>
public sealed class OllamaClient(HttpClient http, IGpuLease lease, IOptions<LlmOptions> options) : ILlmClient
{
    public Task<string> CompleteJsonAsync(LlmRequest request, CancellationToken ct = default) => CompleteAsync(request, json: true, ct);

    public Task<string> CompleteTextAsync(LlmRequest request, CancellationToken ct = default) => CompleteAsync(request, json: false, ct);

    private async Task<string> CompleteAsync(LlmRequest request, bool json, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var settings = options.Value;
        var modelOptions = new JsonObject { ["temperature"] = settings.Temperature };
        if (request.MaxTokens is { } maxTokens)
        {
            modelOptions["num_predict"] = maxTokens;
        }

        var body = new JsonObject
        {
            ["model"] = settings.Model,
            ["stream"] = false,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = request.System },
                new JsonObject { ["role"] = "user", ["content"] = request.User }),
            ["options"] = modelOptions,
        };

        if (json)
        {
            body["format"] = request.Schema.DeepClone();
        }

        if (!string.IsNullOrWhiteSpace(settings.ReasoningEffort))
        {
            body["think"] = !string.Equals(settings.ReasoningEffort, "none", StringComparison.OrdinalIgnoreCase);
        }

        await using var held = await lease.AcquireAsync(GpuConsumer.Llm, ct).ConfigureAwait(false);

        var payload = await OllamaHttp.PostAsync(http, "api/chat", body, "The Ollama endpoint", ct).ConfigureAwait(false);

        return JsonNode.Parse(payload)?["message"]?["content"]?.GetValue<string>()
            ?? throw new InvalidOperationException("The Ollama endpoint returned no message content.");
    }
}

/// <summary>Ollama's native <c>POST api/embed</c>, under the GPU lease.</summary>
public sealed class OllamaEmbeddingClient(HttpClient http, IGpuLease lease, IOptions<LlmOptions> options) : IEmbeddingClient
{
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var body = new JsonObject
        {
            ["model"] = options.Value.EmbeddingModel,
            ["input"] = text,
        };

        await using var held = await lease.AcquireAsync(GpuConsumer.Embeddings, ct).ConfigureAwait(false);

        var payload = await OllamaHttp.PostAsync(http, "api/embed", body, "The Ollama embeddings endpoint", ct).ConfigureAwait(false);

        var vector = JsonNode.Parse(payload)?["embeddings"]?[0]?.AsArray()
            ?? throw new InvalidOperationException("The Ollama embeddings endpoint returned no embedding.");

        return [.. vector.Select(v => (float)v!.GetValue<double>())];
    }
}

internal static class OllamaHttp
{
    public static async Task<string> PostAsync(HttpClient http, string path, JsonObject body, string who, CancellationToken ct)
    {
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(path, content, ct).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{who} answered {(int)response.StatusCode}: {payload[..Math.Min(300, payload.Length)]}");
        }

        return payload;
    }
}
