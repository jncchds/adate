using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Game.Llm;

/// <summary>Someone to write a voice for: who they are, as the story bible already has them.</summary>
public sealed record VoicePerson(string Id, string Name, string WorksAs, IReadOnlyList<string> Temper, IReadOnlyList<string> Likes);

public sealed record VoiceEntry(string Id, string Voice);

public sealed record VoiceResponse(IReadOnlyList<VoiceEntry> People);

/// <summary>
/// How each love interest talks (quality material): written once for the whole cast in one call, so the voices
/// are told apart from each other, and kept with each person. Without an answer, the scenes go on without.
/// </summary>
public sealed class VoiceWriter(ILlmClient llm, IOptions<LlmOptions> options)
{
    public const int MaxLength = 400;

    public const string SystemPrompt =
        "You give the people of a slice-of-life dating sim distinct ways of talking, so a reader could tell them apart " +
        "with the names covered. Stay true to each person's temper and life. Answer with JSON matching the schema.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly JsonObject Schema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["people"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["id"] = new JsonObject { ["type"] = "string" },
                        ["voice"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray("id", "voice"),
                    ["additionalProperties"] = false,
                },
            },
        },
        ["required"] = new JsonArray("people"),
        ["additionalProperties"] = false,
    };

    /// <summary>A voice per person id; people the answer missed are simply left out.</summary>
    public async Task<IReadOnlyDictionary<string, string>> WriteAsync(
        string settingName, string tone, IReadOnlyList<VoicePerson> people, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(people);

        var settings = options.Value;
        if (!settings.Enabled || people.Count == 0)
        {
            return new Dictionary<string, string>();
        }

        var request = $"## Setting\n{settingName}. {tone}\n\n## People\n" + string.Join("\n", people.Select(p =>
            $"- id {p.Id}: {p.Name}, works as {p.WorksAs}. {string.Join(" ", p.Temper)}" +
            (p.Likes.Count > 0 ? $" Likes {string.Join(", ", p.Likes)}." : ""))) +
            "\n\n## Rules\n" +
            $"- For every person, one voice in English, under {MaxLength - 50} characters: how long their sentences run and their rhythm; " +
            "two habits of speech (a word or phrase they lean on, how they address people, what they do with a question they dislike); " +
            "what they bring up without being asked; and one short sample line in quotes.\n" +
            "- Make the voices clearly different from each other. No accents or dialect spelling.\n" +
            "- Use exactly the ids given, once each.";

        for (var attempt = 0; attempt <= settings.MaxRetries; attempt++)
        {
            try
            {
                var raw = await llm.CompleteJsonAsync(new LlmRequest(SystemPrompt, request, "voices", Schema, 2000), ct).ConfigureAwait(false);
                var response = JsonSerializer.Deserialize<VoiceResponse>(raw, Json);
                var ids = people.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);

                var voices = (response?.People ?? [])
                    .Where(e => ids.Contains(e.Id) && !string.IsNullOrWhiteSpace(e.Voice) && e.Voice.Trim().Length <= MaxLength)
                    .GroupBy(e => e.Id, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.First().Voice.Trim(), StringComparer.Ordinal);

                if (voices.Count > 0)
                {
                    return voices;
                }
            }
            catch (Exception ex) when (ex is JsonException or HttpRequestException or InvalidOperationException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                // Tried again; with no answer at all, the scenes go on without voices.
            }
        }

        return new Dictionary<string, string>();
    }
}
