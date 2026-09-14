using System.Text.Json;
using System.Text.Json.Nodes;
using Game.Core.Cast;
using Game.Core.Story;
using Microsoft.Extensions.Options;

namespace Game.Llm;

public sealed record ReactionResponse(string Text, string Expression, IReadOnlyList<string>? Tags);

/// <param name="Tags">What the reply shows about the player: the proposed choice's tags, or the ones read from free text.</param>
public sealed record WrittenReaction(
    string Text,
    string? Expression,
    IReadOnlyList<string> Tags,
    bool Fallback,
    int Attempts,
    IReadOnlyList<string> Rejections);

/// <summary>
/// Writes how the people present react to the player's reply (phase-3 plan: choices). For free text,
/// the model also reads what the reply shows about the player, as tags C# then scores; unknown tags
/// are dropped, never trusted. The reaction may repeat what the player did or said, and nothing more.
/// </summary>
public sealed class ReactionWriter(ILlmClient llm, StoryContent story, CastContent cast, IOptions<LlmOptions> options)
{
    public const int MaxLength = 1000;
    public const int MaxReplyLength = 300;

    public const string SystemPrompt =
        "You continue one scene of a first-person dating sim after the player has replied. Write only how " +
        "the other people present react. Answer with JSON matching the schema.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <param name="playerWords">What the player chose or typed.</param>
    /// <param name="chosenTags">The proposed choice's tags; null for free text, whose tags the model reads.</param>
    public async Task<WrittenReaction> WriteAsync(
        ScenePacket packet,
        string sceneText,
        string playerWords,
        IReadOnlyList<string>? chosenTags,
        string fallbackText,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentException.ThrowIfNullOrWhiteSpace(playerWords);

        var settings = options.Value;
        if (!settings.Enabled)
        {
            return new WrittenReaction(fallbackText, null, chosenTags ?? [], Fallback: true, Attempts: 0, []);
        }

        var request = ScenePacketBuilder.Render(packet)
            + "\n## The scene so far\n" + sceneText
            + "\n\n## What the player does or says\n" + playerWords
            + "\n\n## Now\n"
            + "- Write how the people present react: one or two short paragraphs, in the second person as before.\n"
            + "- Take the player's reply exactly as written above. Add nothing else the player does, says, thinks or feels.\n"
            + (chosenTags is null
                ? "- tags: what the player's reply shows about them, from the schema's list; an empty list if nothing stands out.\n"
                : "- tags: an empty list.\n");

        var schema = Schema(packet);
        var rejections = new List<string>();
        IReadOnlyList<string> lastReasons = [];
        var attempts = 0;

        for (var attempt = 0; attempt <= settings.MaxRetries; attempt++)
        {
            attempts++;
            var user = lastReasons.Count == 0
                ? request
                : request + "\n\n## Your previous answer was rejected\n" + string.Join("\n", lastReasons.Select(r => "- " + r));

            ReactionResponse? response;
            try
            {
                var raw = await llm.CompleteJsonAsync(new LlmRequest(SystemPrompt, user, "reaction", schema), ct).ConfigureAwait(false);
                response = JsonSerializer.Deserialize<ReactionResponse>(raw, Json);
            }
            catch (JsonException ex)
            {
                lastReasons = [$"The answer was not valid JSON for the schema: {ex.Message}"];
                rejections.Add($"attempt {attempts}: {lastReasons[0]}");
                continue;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                rejections.Add($"attempt {attempts}: the model could not answer ({ex.Message})");
                lastReasons = [];
                continue;
            }

            var reasons = Check(response, packet, playerWords);
            if (reasons.Count == 0)
            {
                var tags = chosenTags
                    ?? [.. (response!.Tags ?? []).Distinct(StringComparer.Ordinal).Where(t => story.IsKnownTag(t, cast))];

                return new WrittenReaction(response!.Text.Trim(), response.Expression, tags, Fallback: false, attempts, rejections);
            }

            lastReasons = reasons;
            rejections.AddRange(reasons.Select(r => $"attempt {attempts}: {r}"));
        }

        return new WrittenReaction(fallbackText, null, chosenTags ?? [], Fallback: true, attempts, rejections);
    }

    public JsonObject Schema(ScenePacket packet)
    {
        static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode)JsonValue.Create(v)!)]);

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["text"] = new JsonObject { ["type"] = "string" },
                ["expression"] = new JsonObject { ["type"] = "string", ["enum"] = Strings(packet.Expressions) },
                ["tags"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string", ["enum"] = Strings(story.ChoiceTags()) } },
            },
            ["required"] = Strings(["text", "expression", "tags"]),
            ["additionalProperties"] = false,
        };
    }

    private static List<string> Check(ReactionResponse? response, ScenePacket packet, string playerWords)
    {
        var reasons = new List<string>();

        if (response is null || string.IsNullOrWhiteSpace(response.Text))
        {
            reasons.Add("The reaction is empty.");
            return reasons;
        }

        if (response.Text.Length > MaxLength)
        {
            reasons.Add($"The reaction is {response.Text.Length} characters; keep it under {MaxLength}.");
        }

        if (Narration.Unfinished(response.Text))
        {
            reasons.Add("The reaction stops mid-sentence or leaves a quote open. Finish it.");
        }

        var firstPerson = Narration.FirstPersonOutsideDialogue(response.Text);
        if (firstPerson.Count >= Narration.FirstPersonTolerance)
        {
            reasons.Add($"The narration slips into the first person ({string.Join(", ", firstPerson.Distinct().Take(5))}). Write in the second person.");
        }

        // Restating the player's own reply is allowed; anything they did not write is not.
        var added = Narration.PlayerActions(response.Text)
            .Where(action => !playerWords.Contains(action.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1], StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToList();

        if (added.Count > 0)
        {
            reasons.Add($"The reaction adds things the player did not choose ({string.Join("; ", added.Take(4))}). Describe only how the others react.");
        }

        if (!packet.Expressions.Contains(response.Expression, StringComparer.OrdinalIgnoreCase))
        {
            reasons.Add($"The expression '{response.Expression}' is not one of {string.Join(", ", packet.Expressions)}.");
        }

        return reasons;
    }
}
