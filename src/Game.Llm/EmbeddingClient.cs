using System.Text;
using System.Text.Json.Nodes;
using Game.Core.Gpu;
using Microsoft.Extensions.Options;

namespace Game.Llm;

/// <summary>Turns a memory summary into a vector for retrieval (plan §8).</summary>
public interface IEmbeddingClient
{
    /// <summary>The embedding. Throws when the endpoint cannot be reached or answers with an error.</summary>
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
}

/// <summary><c>POST embeddings</c>, the OpenAI-compatible shape LM Studio and llama.cpp share, under the GPU lease.</summary>
public sealed class OpenAiCompatibleEmbeddingClient(HttpClient http, IGpuLease lease, IOptions<LlmOptions> options) : IEmbeddingClient
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

        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("embeddings", content, ct).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"The embeddings endpoint answered {(int)response.StatusCode}: {payload[..Math.Min(300, payload.Length)]}");
        }

        var vector = JsonNode.Parse(payload)?["data"]?[0]?["embedding"]?.AsArray()
            ?? throw new InvalidOperationException("The embeddings endpoint returned no embedding.");

        return [.. vector.Select(v => (float)v!.GetValue<double>())];
    }
}
