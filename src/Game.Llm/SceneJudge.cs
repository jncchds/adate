using System.Text.Json;
using System.Text.Json.Nodes;
using Game.Core.Story;

namespace Game.Llm;

/// <summary>
/// The optional short judge (plan §8): a second call that reads the written prose against the facts
/// that cannot change. The validator checks the JSON; only this catches prose that says someone has
/// blue hair when their hair is black. A judge that fails to answer passes the scene, so it can only
/// make a scene better, never block one.
/// </summary>
public sealed class SceneJudge(ILlmClient llm)
{
    public const string SystemPrompt =
        "You check one scene of a story against facts that cannot change. List each statement in the " +
        "scene that contradicts one of the facts, quoting the statement and naming the fact. List " +
        "nothing else. The scene may be written in another language than the facts; compare meaning, not wording. " +
        "Answer with JSON matching the schema, with an empty list when nothing contradicts.";

    private static readonly JsonObject Schema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["contradictions"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
        },
        ["required"] = new JsonArray("contradictions"),
        ["additionalProperties"] = false,
    };

    /// <param name="names">Display names by id, so the judge reads "Rin", not a guid.</param>
    public async Task<IReadOnlyList<string>> CheckAsync(
        string text,
        IReadOnlyList<KnownFact> facts,
        IReadOnlyDictionary<string, string> names,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(names);

        if (facts.Count == 0 || string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var lines = facts.Select(f =>
            $"- {names.GetValueOrDefault(f.Fact.Subject, f.Fact.Subject)} {f.Fact.Predicate} {names.GetValueOrDefault(f.Fact.Object, f.Fact.Object)}");
        var user = "## Facts\n" + string.Join("\n", lines) + "\n\n## Scene\n" + text;

        try
        {
            var raw = await llm.CompleteJsonAsync(new LlmRequest(SystemPrompt, user, "judge", Schema), ct).ConfigureAwait(false);
            var found = JsonNode.Parse(raw)?["contradictions"]?.AsArray() ?? [];

            return [.. found.Select(n => n?.GetValue<string>()).OfType<string>().Where(s => !string.IsNullOrWhiteSpace(s))];
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or FormatException
                                   || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return [];
        }
    }
}
