using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Game.Llm.Providers;

/// <summary>
/// Gemini through Google AI Studio: <c>POST models/{model}:generateContent</c>, with the schema as
/// <c>responseJsonSchema</c>. A hosted model shares no GPU with the pictures, so no lease is held.
/// </summary>
public sealed class GoogleAiClient(HttpClient http, IOptions<LlmOptions> options) : ILlmClient
{
    public Task<string> CompleteJsonAsync(LlmRequest request, CancellationToken ct = default) => CompleteAsync(request, json: true, ct);

    public Task<string> CompleteTextAsync(LlmRequest request, CancellationToken ct = default) => CompleteAsync(request, json: false, ct);

    private async Task<string> CompleteAsync(LlmRequest request, bool json, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var settings = options.Value;
        var generation = new JsonObject { ["temperature"] = settings.Temperature };

        if (request.MaxTokens is { } maxTokens)
        {
            generation["maxOutputTokens"] = maxTokens;
        }

        if (json)
        {
            generation["responseMimeType"] = "application/json";
            generation["responseJsonSchema"] = request.Schema.DeepClone();
        }

        if (ThinkingBudget(settings.ReasoningEffort) is { } budget)
        {
            generation["thinkingConfig"] = new JsonObject { ["thinkingBudget"] = budget };
        }

        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = request.System }) },
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = request.User }),
            }),
            ["generationConfig"] = generation,
        };

        var payload = await GoogleAiHttp.PostAsync(http, settings, $"{GoogleAiHttp.ModelPath(settings.Model)}:generateContent", body, ct).ConfigureAwait(false);
        var root = JsonNode.Parse(payload);

        // A blocked prompt is a 200 with no candidates, and a candidate cut off early can come back without parts.
        if (root?["candidates"]?[0] is not { } candidate)
        {
            var reason = root?["promptFeedback"]?["blockReason"]?.GetValue<string>();
            throw new InvalidOperationException(reason is null
                ? "Google AI Studio returned no candidates."
                : $"Google AI Studio blocked the prompt ({reason}).");
        }

        var text = string.Concat(
            (candidate["content"]?["parts"]?.AsArray() ?? [])
                .Where(part => part?["thought"]?.GetValue<bool>() != true)
                .Select(part => part?["text"]?.GetValue<string>()));

        return text.Length > 0
            ? text
            : throw new InvalidOperationException(
                $"Google AI Studio returned no message content (finish reason {candidate["finishReason"]?.GetValue<string>() ?? "unknown"}).");
    }

    /// <summary>
    /// <c>none</c> turns thinking off where the model allows it (the Flash models; Pro models refuse a zero budget);
    /// empty leaves the model's default.
    /// </summary>
    private static int? ThinkingBudget(string? effort) => effort?.Trim().ToLowerInvariant() switch
    {
        "none" => 0,
        "minimal" or "low" => 1024,
        "medium" => 8192,
        "high" => 24576,
        _ => null,
    };
}

/// <summary>Google AI Studio's <c>POST models/{model}:embedContent</c>.</summary>
public sealed class GoogleAiEmbeddingClient(HttpClient http, IOptions<LlmOptions> options) : IEmbeddingClient
{
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var settings = options.Value;
        var body = new JsonObject
        {
            ["content"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) },
        };

        var payload = await GoogleAiHttp.PostAsync(http, settings, $"{GoogleAiHttp.ModelPath(settings.EmbeddingModel)}:embedContent", body, ct).ConfigureAwait(false);

        var vector = JsonNode.Parse(payload)?["embedding"]?["values"]?.AsArray()
            ?? throw new InvalidOperationException("Google AI Studio returned no embedding.");

        return [.. vector.Select(v => (float)v!.GetValue<double>())];
    }
}

internal static class GoogleAiHttp
{
    /// <summary>Model ids are accepted with or without the <c>models/</c> prefix Google lists them with.</summary>
    public static string ModelPath(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException("Google AI Studio needs a model id, such as gemini-2.5-flash.");
        }

        var id = model.Trim();
        return id.StartsWith("models/", StringComparison.Ordinal) ? id : "models/" + id;
    }

    public static async Task<string> PostAsync(HttpClient http, LlmOptions settings, string path, JsonObject body, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            message.Headers.Add("x-goog-api-key", settings.ApiKey.Trim());
        }

        using var response = await http.SendAsync(message, ct).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Google AI Studio answered {(int)response.StatusCode}: {payload[..Math.Min(300, payload.Length)]}");
        }

        return payload;
    }
}
