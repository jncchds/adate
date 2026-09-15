using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Game.Core.Gpu;
using Microsoft.Extensions.Options;

namespace Game.Llm.Providers;

/// <summary>
/// <c>POST chat/completions</c> with a <c>json_schema</c> response format, the shape LM Studio, llama.cpp,
/// vLLM and OpenAI itself share. A key, when configured, goes as a bearer token. Calls to a server of
/// your own hold the GPU lease (HANDOFF 1.6); calls to OpenAI do not.
/// </summary>
public sealed class OpenAiCompatibleClient(HttpClient http, IGpuLease lease, IOptions<LlmOptions> options) : ILlmClient
{
    public Task<string> CompleteJsonAsync(LlmRequest request, CancellationToken ct = default) => CompleteAsync(request, json: true, ct);

    public Task<string> CompleteTextAsync(LlmRequest request, CancellationToken ct = default) => CompleteAsync(request, json: false, ct);

    private async Task<string> CompleteAsync(LlmRequest request, bool json, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var settings = options.Value;
        var body = new JsonObject
        {
            ["model"] = settings.Model,
            ["temperature"] = settings.Temperature,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = request.System },
                new JsonObject { ["role"] = "user", ["content"] = request.User }),
        };

        if (json)
        {
            body["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = request.SchemaName,
                    ["strict"] = true,
                    ["schema"] = request.Schema.DeepClone(),
                },
            };
        }

        if (request.MaxTokens is { } maxTokens)
        {
            // OpenAI's reasoning models refuse max_tokens; local servers do not all know its replacement.
            body[settings.Provider == LlmProviderType.OpenAi ? "max_completion_tokens" : "max_tokens"] = maxTokens;
        }

        // Google's compatibility layer turns the effort into a thinking budget, which Gemma refuses.
        if (!string.IsNullOrWhiteSpace(settings.ReasoningEffort)
            && !(LlmProviders.IsGoogle(settings.ChatAddress) && LlmProviders.IsGemma(settings.Model)))
        {
            body["reasoning_effort"] = settings.ReasoningEffort;
        }

        await using var held = LlmProviders.UsesLocalGpu(settings.Provider)
            ? await lease.AcquireAsync(GpuConsumer.Llm, ct).ConfigureAwait(false)
            : null;

        var payload = await OpenAiHttp.PostAsync(http, settings, "chat/completions", body, "The LLM endpoint", ct).ConfigureAwait(false);

        var content = JsonNode.Parse(payload)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
            ?? throw new InvalidOperationException("The LLM endpoint returned no message content.");

        return WithoutThought(content);
    }

    /// <summary>Google's compatibility layer puts Gemma's reasoning in the content, as a leading <c>&lt;thought&gt;</c> block.</summary>
    private static string WithoutThought(string content)
    {
        const string Close = "</thought>";

        if (!content.TrimStart().StartsWith("<thought>", StringComparison.Ordinal))
        {
            return content;
        }

        var end = content.IndexOf(Close, StringComparison.Ordinal);
        return end < 0 ? content : content[(end + Close.Length)..].TrimStart();
    }
}

/// <summary><c>POST embeddings</c>, the OpenAI-compatible shape LM Studio, llama.cpp and OpenAI share.</summary>
public sealed class OpenAiCompatibleEmbeddingClient(HttpClient http, IGpuLease lease, IOptions<LlmOptions> options) : IEmbeddingClient
{
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var settings = options.Value;
        var body = new JsonObject
        {
            ["model"] = settings.EmbeddingModel,
            ["input"] = text,
        };

        await using var held = LlmProviders.UsesLocalGpu(settings.Provider)
            ? await lease.AcquireAsync(GpuConsumer.Embeddings, ct).ConfigureAwait(false)
            : null;

        var payload = await OpenAiHttp.PostAsync(http, settings, "embeddings", body, "The embeddings endpoint", ct).ConfigureAwait(false);

        var vector = JsonNode.Parse(payload)?["data"]?[0]?["embedding"]?.AsArray()
            ?? throw new InvalidOperationException("The embeddings endpoint returned no embedding.");

        return [.. vector.Select(v => (float)v!.GetValue<double>())];
    }
}

internal static class OpenAiHttp
{
    public static async Task<string> PostAsync(HttpClient http, LlmOptions settings, string path, JsonObject body, string who, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey.Trim());
        }

        using var response = await http.SendAsync(message, ct).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{who} answered {(int)response.StatusCode}: {payload[..Math.Min(300, payload.Length)]}");
        }

        return payload;
    }
}
