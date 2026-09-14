using System.Text.Json;
using System.Text.Json.Nodes;
using Game.Core.Cast;
using Game.Core.Story;
using Microsoft.Extensions.Options;

namespace Game.Llm;

public sealed record ReactionResponse(
    string Text,
    string Expression,
    IReadOnlyList<string>? Tags,
    ProposedMeeting? Meet = null,
    bool? Ends = null,
    IReadOnlyList<SceneResponseChoice>? Choices = null,
    bool? Numbers = null);

/// <param name="Tags">What the reply shows about the player: the proposed choice's tags, or the ones read from free text.</param>
/// <param name="Meet">A meeting the two just agreed on, unchecked; C# decides whether it becomes a promise.</param>
public sealed record WrittenReaction(
    string Text,
    string? Expression,
    IReadOnlyList<string> Tags,
    bool Fallback,
    int Attempts,
    IReadOnlyList<string> Rejections,
    ProposedMeeting? Meet = null,
    bool Ends = true,
    IReadOnlyList<ProposedChoice>? Choices = null,
    bool ExchangedNumbers = false);

/// <summary>
/// Writes how the people present react to the player's reply (phase-3 plan: choices). For free text,
/// the model also reads what the reply shows about the player, as tags C# then scores; unknown tags
/// are dropped, never trusted. The reaction may repeat what the player did or said, and nothing more.
/// </summary>
/// <param name="judge">Reads a reaction in another language for things the player did not choose; none in English, which has word checks.</param>
public sealed class ReactionWriter(ILlmClient llm, StoryContent story, CastContent cast, IOptions<LlmOptions> options, SceneJudge? judge = null)
{
    public const int MaxLength = 1000;
    public const int MaxReplyLength = 300;

    /// <summary>About twice what a 1000-character reaction and its tags take.</summary>
    public const int MaxTokens = 700;

    /// <summary>Other scripts take several tokens a word, as for scenes.</summary>
    public const int MaxTokensOtherLanguages = 1400;

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
        int replyNumber = 1,
        int maxReplies = 1,
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
                ? "- tags: what the player's reply shows about them, from the schema's list; an empty list if nothing stands out. " +
                  "If the reply does or admits something from the dealbreaker tags (lie, two-timing, cruel, stood-up, pushy), include that tag even when it is said honestly.\n"
                : "- tags: an empty list.\n")
            + "- meet: only if the two of them have just agreed to meet again at a set time: the place (one the player knows), " +
              $"in how many days (1 to {MeetingAgreement.MaxDaysAhead}) and the time of day (Morning, Midday, Afternoon or Evening). Otherwise null.\n"
            + "- numbers: true only if, in this reaction, the other person actually gives the player their phone number or the two swap numbers. " +
              "Whether they do is theirs to decide, from their temper and how well they know the player; they may say no or not yet. Otherwise false.\n"
            + Conversation(replyNumber, maxReplies);

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
                var maxTokens = NarrationLanguage.IsEnglish(packet.Language) ? MaxTokens : MaxTokensOtherLanguages;
                var raw = await llm.CompleteJsonAsync(new LlmRequest(SystemPrompt, user, "reaction", schema, maxTokens), ct).ConfigureAwait(false);
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
            if (reasons.Count == 0 && judge is not null && settings.UseJudge && !NarrationLanguage.IsEnglish(packet.Language))
            {
                // Other languages have no word checks; the judge reads for what the player did not choose.
                var verdict = await judge.ReviewAsync(response!.Text, [], Names(packet), agency: true, playerWords, ct).ConfigureAwait(false);
                if (verdict.PlayerActions.Count > 0)
                {
                    reasons.Add($"The reaction adds things the player did not choose ({string.Join("; ", verdict.PlayerActions.Take(4))}). Describe only how the others react.");

                    // As for scenes: on the last attempt the judge's doubt alone keeps the reaction.
                    if (attempt == settings.MaxRetries)
                    {
                        rejections.Add($"attempt {attempts}: kept despite the judge: {reasons[0]}");
                        reasons.Clear();
                    }
                }
            }

            if (reasons.Count == 0)
            {
                var tags = chosenTags
                    ?? [.. (response!.Tags ?? []).Distinct(StringComparer.Ordinal).Where(t => story.IsKnownTag(t, cast))];

                // The conversation goes on only with usable replies to offer; otherwise this answer closes it,
                // and the reaction itself is still kept.
                var nextReasons = new List<string>();
                var next = replyNumber < maxReplies && response!.Ends is false
                    ? SceneWriter.CheckChoices(response.Choices, story, cast, nextReasons)
                    : [];
                var goesOn = next.Count >= SceneWriter.MinChoices && nextReasons.Count == 0;

                return new WrittenReaction(
                    response!.Text.Trim(), response.Expression, tags, Fallback: false, attempts, rejections, response.Meet,
                    Ends: !goesOn, Choices: goesOn ? next : [], ExchangedNumbers: response.Numbers is true);
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
                ["ends"] = new JsonObject { ["type"] = "boolean" },
                ["numbers"] = new JsonObject { ["type"] = "boolean" },
                ["choices"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["text"] = new JsonObject { ["type"] = "string" },
                            ["tags"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string", ["enum"] = Strings(story.ChoiceTags()) } },
                        },
                        ["required"] = Strings(["text", "tags"]),
                        ["additionalProperties"] = false,
                    },
                },
                ["meet"] = new JsonObject
                {
                    ["type"] = Strings(["object", "null"]),
                    ["properties"] = new JsonObject
                    {
                        ["place"] = new JsonObject { ["type"] = "string" },
                        ["inDays"] = new JsonObject { ["type"] = "integer" },
                        ["slot"] = new JsonObject { ["type"] = "string", ["enum"] = Strings(["Morning", "Midday", "Afternoon", "Evening"]) },
                    },
                    ["required"] = Strings(["place", "inDays", "slot"]),
                    ["additionalProperties"] = false,
                },
            },
            ["required"] = Strings(["text", "expression", "tags", "meet", "numbers", "ends", "choices"]),
            ["additionalProperties"] = false,
        };
    }

    /// <summary>Whether the moment may go on, and what the player could say next when it does.</summary>
    private static string Conversation(int replyNumber, int maxReplies) =>
        replyNumber >= maxReplies
            ? "- ends: true. This is the player's last reply here: close the moment naturally, without deciding anything for the player.\n"
              + "- choices: an empty list.\n"
            : $"- This is the player's reply {replyNumber} of at most {maxReplies} in this scene.\n"
              + "- ends: true when the moment has run its course or someone has to go; otherwise false, ending on something the player can answer.\n"
              + "- choices: when ends is false, two or three short, different things the player could say or do next, in the player's own voice, "
              + "each tagged from the list with what it shows about the player (at most one may be helps:{want} or hinders:{want}); an empty list when ends is true.\n";

    private static IReadOnlyDictionary<string, string> Names(ScenePacket packet)
    {
        var names = packet.Present.ToDictionary(p => p.Id, p => p.Name, StringComparer.Ordinal);
        names[FactLedger.Player] = packet.PlayerName;
        return names;
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

        // The first-person and player-action checks read English words; other languages go without.
        var english = NarrationLanguage.IsEnglish(packet.Language);

        if (Narration.Unfinished(response.Text, english))
        {
            reasons.Add("The reaction stops mid-sentence or leaves a quote open. Finish it.");
        }

        var firstPerson = english ? Narration.FirstPersonOutsideDialogue(response.Text) : [];
        if (firstPerson.Count >= Narration.FirstPersonTolerance)
        {
            reasons.Add($"The narration slips into the first person ({string.Join(", ", firstPerson.Distinct().Take(5))}). Write in the second person.");
        }

        // Restating the player's own reply is allowed; anything they did not write is not.
        var added = (english ? Narration.PlayerActions(response.Text) : [])
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
