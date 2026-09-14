using System.Text.Json;
using System.Text.Json.Nodes;
using Game.Core.Story;

namespace Game.Llm;

/// <summary>
/// Folds several memories into one summary (plan §8: scenes into days, days into weeks). Without a
/// usable answer the summaries are joined as they are, so compaction never loses what happened.
/// </summary>
public sealed class MemoryCompactor(ILlmClient llm)
{
    public const int MaxSummaryLength = 400;

    public const string SystemPrompt =
        "You compress a list of remembered moments from a story into one or two sentences that keep who " +
        "was involved and what changed between them. Answer with JSON matching the schema.";

    private static readonly JsonObject Schema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["summary"] = new JsonObject { ["type"] = "string" } },
        ["required"] = new JsonArray("summary"),
        ["additionalProperties"] = false,
    };

    public async Task<string> SummariseAsync(IReadOnlyList<MemoryEntry> members, bool useModel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(members);

        var joined = string.Join(" ", members.Select(m => m.Summary.Trim()));
        var fallback = joined.Length <= MaxSummaryLength ? joined : joined[..(MaxSummaryLength - 1)] + "…";

        if (!useModel || members.Count == 0)
        {
            return fallback;
        }

        try
        {
            var user = string.Join("\n", members.Select(m => $"- day {m.Day}: {m.Summary}"));
            var raw = await llm.CompleteJsonAsync(new LlmRequest(SystemPrompt, user, "summary", Schema), ct).ConfigureAwait(false);
            var summary = JsonNode.Parse(raw)?["summary"]?.GetValue<string>()?.Trim();

            return string.IsNullOrWhiteSpace(summary) || summary.Length > MaxSummaryLength ? fallback : summary;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or FormatException
                                   || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return fallback;
        }
    }
}
