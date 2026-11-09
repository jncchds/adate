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

        var thinking = !LlmProviders.IsGemma(settings.Model) ? ThinkingBudget(settings.ReasoningEffort) : null;

        // Thinking tokens are spent from the same output cap: an 8k budget under a 2-3k cap cut the JSON off
        // mid-string and cost a retry on most turns (seen in live play), so the cap is raised by the budget.
        if (request.MaxTokens is { } maxTokens)
        {
            generation["maxOutputTokens"] = maxTokens + (thinking ?? 0);
        }

        if (json)
        {
            generation["responseMimeType"] = "application/json";
            generation["responseJsonSchema"] = request.Schema.DeepClone();
        }

        if (thinking is { } budget)
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
        var id = string.IsNullOrWhiteSpace(model) ? LlmProviders.DefaultGoogleModel : model.Trim();
        return id.StartsWith("models/", StringComparison.Ordinal) ? id : "models/" + id;
    }

    internal const int BusyRetries = 2;

    /// <summary>A rate-limit wait longer than this is not waited out in a turn: the player is told instead.</summary>
    internal static readonly TimeSpan LongestWait = TimeSpan.FromSeconds(15);

    /// <summary>The model is overloaded ("high demand" is common on the free tier): worth trying again shortly.</summary>
    internal static bool IsBusy(System.Net.HttpStatusCode status) => status is System.Net.HttpStatusCode.InternalServerError
        or System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.GatewayTimeout;

    /// <summary>Wait before try number <paramref name="attempt"/> (1-based): 2, 4, 8 s.</summary>
    internal static TimeSpan RetryDelay(int attempt) => TimeSpan.FromSeconds(Math.Pow(2, attempt));

    /// <summary>
    /// Reads a 429. A limit "per day" cannot be waited out; Google's own <c>retryDelay</c> is the wait for a
    /// per-minute one. Every request that gets through counts against the quota, so a daily 429 is never retried.
    /// </summary>
    internal static LlmQuotaException ReadQuota(string payload, TimeSpan? header, string model)
    {
        string? message = null;
        var wait = header;
        var daily = payload.Contains("PerDay", StringComparison.OrdinalIgnoreCase);

        try
        {
            var error = JsonNode.Parse(payload)?["error"];
            message = error?["message"]?.GetValue<string>();
            foreach (var detail in error?["details"]?.AsArray() ?? [])
            {
                var delay = detail?["retryDelay"]?.GetValue<string>();
                if (delay is { Length: > 1 } && delay.EndsWith('s')
                    && double.TryParse(delay[..^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                {
                    wait = TimeSpan.FromSeconds(seconds);
                }
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            // Not Google's usual error body: the raw text below says what there is.
        }

        var first = (message ?? payload).Split('\n')[0].Trim();
        var text = daily
            ? $"Google AI's daily limit for {model} is used up, so the writing is a placeholder until it resets (another model can be chosen in Settings). {first}"
            : $"Google AI is rate-limiting {model}{(wait is { } w ? $" (try again in {Math.Ceiling(w.TotalSeconds)} s)" : "")}. {first}";
        return new LlmQuotaException(text.Length > 400 ? text[..400] : text, wait, daily);
    }
    public static async Task<string> PostAsync(HttpClient http, LlmOptions settings, string path, JsonObject body, CancellationToken ct)
    {
        var json = body.ToJsonString();
        var busy = 0;

        while (true)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };

            if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                message.Headers.Add("x-goog-api-key", settings.ApiKey.Trim());
            }

            using var response = await http.SendAsync(message, ct).ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return payload;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                var quota = ReadQuota(payload, response.Headers.RetryAfter?.Delta, settings.Model);
                if (!quota.Daily && busy < 1 && quota.RetryAfter is { } wait && wait <= LongestWait)
                {
                    busy++;
                    await Task.Delay(wait + TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
                    continue;
                }

                throw quota;
            }

            if (IsBusy(response.StatusCode) && busy < BusyRetries)
            {
                await Task.Delay(RetryDelay(++busy), ct).ConfigureAwait(false);
                continue;
            }

            throw new HttpRequestException(
                $"Google AI Studio answered {(int)response.StatusCode}: {payload[..Math.Min(300, payload.Length)]}",
                null,
                response.StatusCode);
        }
    }
}
