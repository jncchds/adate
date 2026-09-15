using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Game.Llm;

/// <summary>A loose end read from what already happened: whom it is about (a person id, or empty for the player's own) and what it is.</summary>
public sealed record SeededThread(string About, string Text);

public sealed record SeededThreadsResponse(IReadOnlyList<SeededThread> Threads);

/// <summary>
/// Reads the loose ends a save's recent scenes left open, once, so a save started before threads were kept
/// has some from its next scene on. Later loose ends come with each written scene and reaction.
/// </summary>
public sealed class ThreadWriter(ILlmClient llm, IOptions<LlmOptions> options)
{
    public const int MaxSeeded = 6;

    public const string SystemPrompt =
        "You keep continuity notes for a dating sim. From the scenes given, list what is still open. Answer with JSON matching the schema.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly JsonObject Schema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["threads"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["about"] = new JsonObject { ["type"] = "string" },
                        ["text"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray("about", "text"),
                    ["additionalProperties"] = false,
                },
            },
        },
        ["required"] = new JsonArray("threads"),
        ["additionalProperties"] = false,
    };

    /// <param name="scenes">The recent scenes, oldest first, each with its day and what was said.</param>
    /// <param name="people">Who may be named, by id.</param>
    public async Task<IReadOnlyList<SeededThread>> FromHistoryAsync(
        IReadOnlyList<string> scenes, IReadOnlyList<(string Id, string Name)> people, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scenes);
        ArgumentNullException.ThrowIfNull(people);

        if (!options.Value.Enabled || scenes.Count == 0)
        {
            return [];
        }

        var request = "## People\n" + string.Join("\n", people.Select(p => $"- id {p.Id}: {p.Name}")) +
            "\n\n## Scenes, oldest first\n" + string.Join("\n\n---\n\n", scenes) +
            "\n\n## Rules\n" +
            $"- Up to {MaxSeeded} loose ends still open at the end: a question left unanswered, a plan mentioned, something someone said they would do. Leave out anything a later scene settled.\n" +
            "- about: the id of the person it is about, from the list; an empty string for the player's own.\n" +
            "- text: one short sentence in English that names who.";

        try
        {
            var raw = await llm.CompleteJsonAsync(new LlmRequest(SystemPrompt, request, "threads", Schema, 1500), ct).ConfigureAwait(false);
            var ids = people.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);

            return
            [
                .. (JsonSerializer.Deserialize<SeededThreadsResponse>(raw, Json)?.Threads ?? [])
                    .Where(t => !string.IsNullOrWhiteSpace(t.Text) && (string.IsNullOrEmpty(t.About) || ids.Contains(t.About)))
                    .Select(t => t with { Text = t.Text.Trim(), About = t.About ?? "" })
                    .Take(MaxSeeded),
            ];
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or InvalidOperationException
                                   || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return [];
        }
    }
}
