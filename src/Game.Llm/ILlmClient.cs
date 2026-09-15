using System.Text.Json.Nodes;

namespace Game.Llm;

/// <param name="Schema">The JSON schema the answer must match; servers that support it constrain decoding to it.</param>
/// <param name="MaxTokens">
/// A cap on the answer. With the context shared across parallel slots, an answer that runs on can
/// exceed it after minutes; a cap ends it quickly so the retry comes sooner.
/// </param>
public sealed record LlmRequest(string System, string User, string SchemaName, JsonObject Schema, int? MaxTokens = null);

/// <summary>One chat completion. Most calls answer in JSON; prose written on its own answers in plain text.</summary>
public interface ILlmClient
{
    /// <summary>The raw JSON text of the answer. Throws when the endpoint cannot be reached or answers with an error.</summary>
    Task<string> CompleteJsonAsync(LlmRequest request, CancellationToken ct = default);

    /// <summary>
    /// The answer as plain text, with no schema constraining it: prose written before its data is read out
    /// (two-pass writing). The request's schema is ignored. Clients without a plain mode answer as for JSON.
    /// </summary>
    Task<string> CompleteTextAsync(LlmRequest request, CancellationToken ct = default) => CompleteJsonAsync(request, ct);
}
