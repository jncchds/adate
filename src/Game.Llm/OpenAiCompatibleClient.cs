using System.Text;
using System.Text.Json.Nodes;
using Game.Core.Gpu;
using Microsoft.Extensions.Options;

namespace Game.Llm;

/// <summary>
/// <c>POST chat/completions</c> with a <c>json_schema</c> response format, the shape LM Studio,
/// llama.cpp and vLLM share. Every call holds the GPU lease (HANDOFF 1.6).
/// </summary>
public sealed class OpenAiCompatibleClient(HttpClient http, IGpuLease lease, IOptions<LlmOptions> options) : ILlmClient
{
    public const string HttpClientName = "llm";

    public async Task<string> CompleteJsonAsync(LlmRequest request, CancellationToken ct = default)
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
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = request.SchemaName,
                    ["strict"] = true,
                    ["schema"] = request.Schema.DeepClone(),
                },
            },
        };

        if (request.MaxTokens is { } maxTokens)
        {
            body["max_tokens"] = maxTokens;
        }

        if (!string.IsNullOrWhiteSpace(settings.ReasoningEffort))
        {
            body["reasoning_effort"] = settings.ReasoningEffort;
        }

        await using var held = await lease.AcquireAsync(GpuConsumer.Llm, ct).ConfigureAwait(false);

        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("chat/completions", content, ct).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"The LLM endpoint answered {(int)response.StatusCode}: {payload[..Math.Min(300, payload.Length)]}");
        }

        return JsonNode.Parse(payload)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
            ?? throw new InvalidOperationException("The LLM endpoint returned no message content.");
    }

    public static Uri EnsureTrailingSlash(string address) =>
        new(address.EndsWith('/') ? address : address + "/");
}
