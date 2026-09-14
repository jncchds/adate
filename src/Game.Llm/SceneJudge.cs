using System.Text.Json;
using System.Text.Json.Nodes;
using Game.Core.Story;

namespace Game.Llm;

/// <summary>What the judge found: statements against the facts, and narration that acts for the player.</summary>
public sealed record JudgeVerdict(IReadOnlyList<string> Contradictions, IReadOnlyList<string> PlayerActions)
{
    public static readonly JudgeVerdict Clear = new([], []);
}

/// <summary>
/// The optional short judge (plan §8): a second call that reads the written prose against the facts
/// that cannot change. The validator checks the JSON; only this catches prose that says someone has
/// blue hair when their hair is black. A judge that fails to answer passes the scene, so it can only
/// make a scene better, never block one.
/// </summary>
/// <remarks>
/// For stories not in English the word checks for the player's agency cannot run, so the judge also
/// reads for narration that says what the player does, says, decides, thinks or feels, or gives them
/// things to hold (phase-3 plan: the rule is checked, not just asked for).
/// </remarks>
public sealed class SceneJudge(ILlmClient llm)
{
    public const string SystemPrompt =
        "You check one scene of a story against facts that cannot change. List each statement in the " +
        "scene that contradicts one of the facts, quoting the statement and naming the fact. List " +
        "nothing else. The scene may be written in another language than the facts; compare meaning, not wording. " +
        "Answer with JSON matching the schema, with an empty list when nothing contradicts.";

    public const string AgencyPrompt =
        " Also list as playerActions, quoting each, every sentence of narration outside quoted dialogue that says what " +
        "the player (addressed as \"you\") does, says, decides, thinks or feels, or that gives the player something to hold, " +
        "wear or carry. What the player sees, hears or notices is fine. When the player's own reply is given, restating " +
        "it is fine. An empty list when there is none.";

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

    private static readonly JsonObject AgencySchema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["contradictions"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
            ["playerActions"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
        },
        ["required"] = new JsonArray("contradictions", "playerActions"),
        ["additionalProperties"] = false,
    };

    /// <param name="names">Display names by id, so the judge reads "Rin", not a guid.</param>
    public async Task<IReadOnlyList<string>> CheckAsync(
        string text,
        IReadOnlyList<KnownFact> facts,
        IReadOnlyDictionary<string, string> names,
        CancellationToken ct = default) =>
        (await ReviewAsync(text, facts, names, agency: false, ct: ct).ConfigureAwait(false)).Contradictions;

    /// <param name="agency">Also read for narration that acts for the player: set for stories not in English.</param>
    /// <param name="playerWords">The player's own reply, which a reaction may restate.</param>
    public async Task<JudgeVerdict> ReviewAsync(
        string text,
        IReadOnlyList<KnownFact> facts,
        IReadOnlyDictionary<string, string> names,
        bool agency,
        string? playerWords = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(names);

        if (string.IsNullOrWhiteSpace(text) || (facts.Count == 0 && !agency))
        {
            return JudgeVerdict.Clear;
        }

        var lines = facts.Select(f =>
            $"- {names.GetValueOrDefault(f.Fact.Subject, f.Fact.Subject)} {f.Fact.Predicate} {names.GetValueOrDefault(f.Fact.Object, f.Fact.Object)}");
        var user = "## Facts\n" + (facts.Count == 0 ? "- none" : string.Join("\n", lines))
            + (agency && !string.IsNullOrWhiteSpace(playerWords) ? "\n\n## The player's own reply\n" + playerWords : "")
            + "\n\n## Scene\n" + text;

        try
        {
            var request = agency
                ? new LlmRequest(SystemPrompt + AgencyPrompt, user, "judge", AgencySchema)
                : new LlmRequest(SystemPrompt, user, "judge", Schema);
            var raw = await llm.CompleteJsonAsync(request, ct).ConfigureAwait(false);
            var answer = JsonNode.Parse(raw);

            return new JudgeVerdict(Strings(answer?["contradictions"]), agency ? Strings(answer?["playerActions"]) : []);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or FormatException
                                   || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return JudgeVerdict.Clear;
        }
    }

    private static IReadOnlyList<string> Strings(JsonNode? node) =>
        [.. (node?.AsArray() ?? []).Select(n => n?.GetValue<string>()).OfType<string>().Where(s => !string.IsNullOrWhiteSpace(s))];
}
