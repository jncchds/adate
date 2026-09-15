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
    bool? Numbers = null,
    IReadOnlyList<string>? Threads = null,
    IReadOnlyList<long>? Resolved = null,
    IReadOnlyList<SceneResponsePlace>? Places = null,
    IReadOnlyList<ProposedRoutine>? Routines = null,
    SceneResponseOutfit? Outfit = null);

/// <param name="Tags">What the reply shows about the player: the proposed choice's tags, or the ones read from free text.</param>
/// <param name="Meet">A meeting the two just agreed on, unchecked; C# decides whether it becomes a promise.</param>
/// <param name="Threads">New loose ends the reaction left open; null when threads are off.</param>
/// <param name="Resolved">Loose ends from the packet the reaction settled.</param>
/// <param name="Places">Places the reaction named, unchecked: C# checks them against the place types and stores the good ones.</param>
/// <param name="Routines">What people said about their own weeks, for C# to check against their schedules.</param>
/// <param name="Outfit">What the main person changed into or put on in the reaction, such as a jacket the player offered; null for no change.</param>
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
    bool ExchangedNumbers = false,
    IReadOnlyList<string>? Threads = null,
    IReadOnlyList<long>? Resolved = null,
    IReadOnlyList<Game.Core.Places.PlaceProposal>? Places = null,
    IReadOnlyList<ProposedRoutine>? Routines = null,
    Outfit? Outfit = null);

/// <summary>
/// Writes how the people present react to the player's reply (phase-3 plan: choices). For free text,
/// the model also reads what the reply shows about the player, as tags C# then scores; unknown tags
/// are dropped, never trusted. The reaction may repeat what the player did or said, and nothing more.
/// With <see cref="LlmOptions.TwoPass"/>, the prose comes first in plain text and its data is read out of it.
/// </summary>
/// <param name="judge">Reads a reaction in another language for things the player did not choose; none in English, which has word checks.</param>
public sealed class ReactionWriter(ILlmClient llm, StoryContent story, CastContent cast, IOptions<LlmOptions> options, SceneJudge? judge = null)
{
    public const int MaxLength = 1000;
    public const int MaxReplyLength = 300;

    /// <summary>Several times what a 1000-character reaction, its tags and the next replies take.</summary>
    public const int MaxTokens = 2000;

    /// <summary>Other scripts take several tokens a word, as for scenes.</summary>
    public const int MaxTokensOtherLanguages = 4000;

    public const string SystemPrompt =
        "You continue one scene of a first-person dating sim after the player has replied. Write only how " +
        "the other people present react. Answer with JSON matching the schema.";

    public const string ProseSystemPrompt =
        "You continue one scene of a first-person dating sim after the player has replied. Write only how " +
        "the other people present react, and answer with that prose alone.";

    public const string ExtractSystemPrompt =
        "You read the continuation of a dating sim scene that has already been written, and fill in its data from " +
        "what it says. Answer with JSON matching the schema.";

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

        // On their own, the player's words are something they do; what follows is how it goes, not anyone's reaction.
        var alone = packet.Present.Count == 0;
        var situation = "\n## The scene so far\n" + sceneText
            + "\n\n## What the player does or says\n" + playerWords
            + "\n\n## Now\n";

        var proseRules =
            (alone
                ? "- Nobody the player knows is here. Write how it goes: what doing this is like here, and what the place, the weather and the people around do. One or two short paragraphs, in the second person as before. Invent nobody the player could get to know.\n"
                : "- Write how the people present react: one or two short paragraphs, in the second person as before.\n")
            + "- Take the player's reply exactly as written above. Add nothing else the player does, says, thinks or feels.\n";

        var fieldRules =
            (chosenTags is not null
                ? "- tags: an empty list.\n"
                : alone
                    ? "- tags: the quality doing this shows about the player, from the desires in the schema's list; an empty list if none does.\n"
                    : "- tags: what the player's reply shows about them, from the schema's list; an empty list if nothing stands out. " +
                      "If the reply does or admits something from the dealbreaker tags (lie, two-timing, cruel, stood-up, pushy), include that tag even when it is said honestly.\n")
            + (alone
                ? "- meet: null.\n- numbers: false.\n- routines: an empty list.\n"
                : "- meet: only if the two of them have just agreed to meet at a set time, or to go somewhere together right now: " +
                  "the place (one the player knows, or one you add to places), " +
                  $"in how many days ({MeetingAgreement.Now} for today, otherwise 1 to {MeetingAgreement.MaxDaysAhead}) " +
                  $"and the time of day: {MeetingAgreement.NowSlot} when they set off together straight away, otherwise Morning, Midday, Afternoon, Evening or Night " +
                  $"(it is {packet.Clock.Slot} now, so later today is a later time with 0 days). Otherwise null.\n"
                  + "- numbers: true only if, in this reaction, the other person actually gives the player their phone number or the two swap numbers. " +
                  "Whether they do is theirs to decide, from their temper and how well they know the player; they may say no or not yet. Otherwise false.\n"
                  + $"- {ScenePacketBuilder.RoutineRule}\n"
                  + (packet.Outfit is { } outfit ? $"- {ScenePacketBuilder.OutfitChangeRule(outfit)}\n" : ""))
            + $"- {ScenePacketBuilder.PlaceRule}\n"
            + Conversation(replyNumber, maxReplies, alone, packet.VariedChoices)
            + (packet.LooseEnds is null ? "" : ScenePacketBuilder.ThreadRules + "\n");

        var twoPass = settings.TwoPass;
        var request = twoPass
            ? ScenePacketBuilder.Render(packet, ScenePart.Prose) + situation + proseRules + "- Answer with the reaction's prose only: no notes, no lists, no JSON.\n"
            : ScenePacketBuilder.Render(packet) + situation + proseRules + fieldRules;

        var schema = Schema(packet, withText: !twoPass);
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
                if (twoPass)
                {
                    var prose = (await llm.CompleteTextAsync(new LlmRequest(ProseSystemPrompt, user, "reaction-prose", new JsonObject(), maxTokens), ct)
                        .ConfigureAwait(false)).Trim();
                    var extract = ScenePacketBuilder.Render(packet, ScenePart.Context) + situation.Replace("## Now\n", "", StringComparison.Ordinal)
                        + "## What was written next\n" + prose
                        + "\n\n## Fill in\n- The continuation above is already written. Do not rewrite it: fill in the fields from what it says.\n"
                        + string.Concat(NarrationLanguage.DataRules(packet.Language, packet.PlayerGender).Select(rule => $"- {rule}\n"))
                        + $"- expression: how the main person here looks at the end, one of {string.Join(", ", packet.Expressions)}.\n"
                        + fieldRules;
                    var raw = await llm.CompleteJsonAsync(new LlmRequest(ExtractSystemPrompt, extract, "reaction", schema, maxTokens), ct).ConfigureAwait(false);
                    response = JsonSerializer.Deserialize<ReactionResponse>(raw, Json) is { } read ? read with { Text = prose } : null;
                }
                else
                {
                    var raw = await llm.CompleteJsonAsync(new LlmRequest(SystemPrompt, user, "reaction", schema, maxTokens), ct).ConfigureAwait(false);
                    response = JsonSerializer.Deserialize<ReactionResponse>(raw, Json);
                }
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
                    Ends: !goesOn, Choices: goesOn ? next : [], ExchangedNumbers: response.Numbers is true,
                    Threads: packet.LooseEnds is null ? null : StoryThreads.Keep(response.Threads),
                    Resolved: SceneWriter.Settled(packet, response.Resolved),
                    Places:
                    [
                        .. (response.Places ?? []).Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Name)).Select(p => new Game.Core.Places.PlaceProposal(
                            p.Type ?? "", p.Name.Trim(), p.Details ?? [], string.IsNullOrWhiteSpace(p.Owner) ? null : p.Owner.Trim(), p.Look)),
                    ],
                    Routines: [.. (response.Routines ?? []).Where(r => r is not null)],
                    Outfit: alone ? null : Outfits.Accept(packet.Outfit, response.Outfit?.Dress, response.Outfit?.Over));
            }

            lastReasons = reasons;
            rejections.AddRange(reasons.Select(r => $"attempt {attempts}: {r}"));
        }

        return new WrittenReaction(fallbackText, null, chosenTags ?? [], Fallback: true, attempts, rejections);
    }

    /// <param name="withText">False when the prose was written separately, and only its data is asked for.</param>
    public JsonObject Schema(ScenePacket packet, bool withText = true)
    {
        ArgumentNullException.ThrowIfNull(packet);

        static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode)JsonValue.Create(v)!)]);

        var properties = new JsonObject
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
                    ["slot"] = new JsonObject { ["type"] = "string", ["enum"] = Strings([MeetingAgreement.NowSlot, "Morning", "Midday", "Afternoon", "Evening", "Night"]) },
                },
                ["required"] = Strings(["place", "inDays", "slot"]),
                ["additionalProperties"] = false,
            },
        };

        properties["places"] = new JsonObject
        {
            ["type"] = "array",
            ["items"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["type"] = new JsonObject { ["type"] = "string" },
                    ["name"] = new JsonObject { ["type"] = "string" },
                    ["details"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                    ["owner"] = new JsonObject { ["type"] = "string" },
                    ["look"] = new JsonObject { ["type"] = "string" },
                },
                ["required"] = Strings(["type", "name", "details", "owner", "look"]),
                ["additionalProperties"] = false,
            },
        };
        properties["routines"] = SceneWriter.RoutineProperty();

        List<string> required = ["text", "expression", "tags", "meet", "numbers", "ends", "choices", "places", "routines"];

        if (packet.Outfit is { } outfit && packet.Present.Count > 0)
        {
            properties["outfit"] = SceneWriter.OutfitProperty(outfit, nullable: true);
            required.Add("outfit");
        }

        if (packet.LooseEnds is not null)
        {
            SceneWriter.ThreadProperties(properties);
            required.AddRange(["threads", "resolved"]);
        }

        if (!withText)
        {
            properties.Remove("text");
            required.Remove("text");
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = Strings(required),
            ["additionalProperties"] = false,
        };
    }

    /// <summary>Whether the moment may go on, and what the player could say next when it does.</summary>
    private static string Conversation(int replyNumber, int maxReplies, bool alone = false, bool varied = false) =>
        replyNumber >= maxReplies
            ? "- ends: true. This is the player's last reply here: close the moment naturally, without deciding anything for the player.\n"
              + "- choices: an empty list.\n"
            : $"- This is the player's reply {replyNumber} of at most {maxReplies} in this scene.\n"
              + (alone
                  ? "- ends: true when there is nothing more to do here for now; otherwise false, ending where the player could do something else.\n"
                    + "- choices: when ends is false, two or three short, different things the player could do next here, in the player's own voice, "
                    + "each tagged with the one quality it shows, from the desires in the list; an empty list when ends is true."
                  : "- ends: true when the moment has run its course or someone has to go; otherwise false, ending on something the player can answer.\n"
                    + "- choices: when ends is false, two or three short, different things the player could say or do next, in the player's own voice, "
                    + "each tagged from the list with what it shows about the player (at most one may be helps:{want} or hinders:{want}); an empty list when ends is true.")
              + (varied ? ScenePacketBuilder.VariedChoiceRule : "") + "\n";

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

        // The game holds meetings only at places it knows: one named nowhere would be agreed and then lost.
        if (response.Meet is { Place: { } meetPlace }
            && !string.IsNullOrWhiteSpace(meetPlace)
            && packet.KnownPlaces is not null
            && !packet.KnownPlaces.Any(known => Game.Core.Places.PlaceProposals.SameName(known, meetPlace))
            && !(response.Places ?? []).Any(p => p is not null && !string.IsNullOrWhiteSpace(p.Name) && Game.Core.Places.PlaceProposals.SameName(p.Name, meetPlace)))
        {
            reasons.Add($"meet names '{meetPlace}', which is not a place the player knows. Use a place they know, or add it to places.");
        }

        return reasons;
    }
}
