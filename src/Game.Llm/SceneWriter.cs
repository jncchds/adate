using System.Text.Json;
using System.Text.Json.Nodes;
using Game.Core.Content;
using Game.Core.Places;
using Game.Core.Story;
using Microsoft.Extensions.Options;

namespace Game.Llm;

public sealed record SceneResponseFact(string Subject, string Predicate, string Object, string Level);

/// <param name="Owner">The id of the person present whose home the place is; empty otherwise.</param>
/// <param name="Look">A few visual phrases for how it looks, in English.</param>
public sealed record SceneResponsePlace(string Type, string Name, IReadOnlyList<string>? Details, string? Owner = null, string? Look = null);

/// <summary>What someone said about their own week, unchecked: C# keeps it only when their schedule agrees.</summary>
public sealed record ProposedRoutine(string Who, string Place, string Slot, string Days);

public sealed record SceneResponseChoice(string Text, IReadOnlyList<string>? Tags);

/// <summary>What the main person wears, unchecked: C# keeps it only when the dress is one it offered.</summary>
public sealed record SceneResponseOutfit(string? Dress, string? Over);

/// <summary>The JSON half of a written scene.</summary>
public sealed record SceneResponse(
    string Text,
    string Expression,
    IReadOnlyList<SceneResponseFact>? Facts,
    IReadOnlyList<SceneResponsePlace>? Places = null,
    string? Summary = null,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyList<SceneResponseChoice>? Choices = null,
    IReadOnlyList<string>? Threads = null,
    IReadOnlyList<long>? Resolved = null,
    IReadOnlyList<ProposedRoutine>? Routines = null,
    SceneResponseOutfit? Outfit = null);

/// <param name="Places">New places the scene named, checked against the place-type catalog.</param>
/// <param name="Fallback">Whether the authored text was used because no answer passed.</param>
/// <param name="Rejections">Why each rejected attempt failed, in order; kept for the turn log.</param>
/// <param name="Summary">One sentence to remember the scene by; null for the fallback.</param>
/// <param name="Tags">Salience tags from <see cref="MemoryTags.All"/>.</param>
/// <param name="Threads">New loose ends the scene left open; null when threads are off.</param>
/// <param name="Resolved">Loose ends from the packet the scene settled.</param>
/// <param name="Routines">What people said about their own weeks, for C# to check against their schedules.</param>
/// <param name="Outfit">What the main person wears, when the answer picked one of the codes offered.</param>
public sealed record WrittenScene(
    string Text,
    string? Expression,
    IReadOnlyList<ProposedFact> Facts,
    IReadOnlyList<PlaceProposal> Places,
    bool Fallback,
    int Attempts,
    IReadOnlyList<string> Rejections,
    string? Summary = null,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyList<ProposedChoice>? Choices = null,
    IReadOnlyList<string>? Threads = null,
    IReadOnlyList<long>? Resolved = null,
    IReadOnlyList<ProposedRoutine>? Routines = null,
    Game.Core.Story.Outfit? Outfit = null);

/// <summary>
/// Writes one scene (plan §8). C# assembles the packet; the model returns prose plus JSON; C# checks
/// it against the state it owns, optionally asks the judge about the prose, and retries with the
/// reasons. After the retries run out, the encounter's authored text is used, so the game never waits
/// on or trusts a bad answer. With <see cref="LlmOptions.TwoPass"/>, the prose is written in plain text first
/// and its data read out of it in a second call.
/// </summary>
public sealed class SceneWriter(
    ILlmClient llm,
    SceneValidator validator,
    StoryContent story,
    Game.Core.Cast.CastContent cast,
    ILocationCatalog placeTypes,
    SceneJudge judge,
    IOptions<LlmOptions> options)
{
    public const string SystemPrompt =
        "You write single scenes for a first-person dating sim. You are given the situation, who is " +
        "present, what each of them knows, and what must happen. Write only that scene, stay inside " +
        "what you are told, and answer with JSON matching the schema.";

    public const string ProseSystemPrompt =
        "You write single scenes for a first-person dating sim. You are given the situation, who is " +
        "present, what each of them knows, and what must happen. Write only that scene, stay inside " +
        "what you are told, and answer with the scene's prose alone.";

    public const string ExtractSystemPrompt =
        "You read a scene of a dating sim that has already been written, and fill in its data from what it says. " +
        "Answer with JSON matching the schema.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A 1500-character scene with its facts, places, summary, tags and choices, with plenty to spare.</summary>
    // Raised from 1500 at the user's request, so no answer is cut off before its JSON closes.
    public const int MaxTokens = 3000;

    /// <summary>
    /// Cyrillic and most other scripts take several tokens a word, and a Ukrainian scene was cut off mid-JSON
    /// at 1500. Still inside the 10K context beside a 3K packet.
    /// </summary>
    public const int MaxTokensOtherLanguages = 5000;

    public const int MinChoices = 2;
    public const int MaxChoices = 3;
    /// <summary>
    /// The longest reply offered. Was 90: varied replies that picked up a loose end ran to 100-110 characters and threw
    /// away half the scenes of a replay for the authored text. A longer one is now left out on its own.
    /// </summary>
    public const int MaxChoiceLength = 160;

    /// <param name="knownPlaces">Places the player knows. A proposal may not repeat one of their names.</param>
    /// <param name="wantChoices">Whether the scene must end with two or three tagged replies for the player.</param>
    public async Task<WrittenScene> WriteAsync(
        ScenePacket packet,
        SceneWorld world,
        string fallbackText,
        IReadOnlyList<string>? knownPlaces = null,
        bool wantChoices = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(world);

        var settings = options.Value;
        if (!settings.Enabled)
        {
            return Fallback(fallbackText, 0, []);
        }

        var twoPass = settings.TwoPass;
        var request = ScenePacketBuilder.Render(packet, twoPass ? ScenePart.Prose : ScenePart.All);
        var extractRequest = twoPass ? ScenePacketBuilder.Render(packet, ScenePart.Extract) : "";
        var schema = Schema(packet, withText: !twoPass);
        var rejections = new List<string>();
        IReadOnlyList<string> lastReasons = [];
        var attempts = 0;

        for (var attempt = 0; attempt <= settings.MaxRetries; attempt++)
        {
            attempts++;
            var feedback = lastReasons.Count == 0
                ? ""
                : "\n\n## Your previous answer was rejected\n" + string.Join("\n", lastReasons.Select(r => "- " + r));

            string raw;
            string? prose = null;
            try
            {
                var maxTokens = NarrationLanguage.IsEnglish(packet.Language) ? MaxTokens : MaxTokensOtherLanguages;
                if (twoPass)
                {
                    prose = (await llm.CompleteTextAsync(new LlmRequest(ProseSystemPrompt, request + feedback, "scene-prose", new JsonObject(), maxTokens), ct)
                        .ConfigureAwait(false)).Trim();
                    raw = await llm.CompleteJsonAsync(
                        new LlmRequest(ExtractSystemPrompt, extractRequest + "\n## The scene as written\n" + prose, "scene", schema, maxTokens), ct).ConfigureAwait(false);
                }
                else
                {
                    raw = await llm.CompleteJsonAsync(new LlmRequest(SystemPrompt, request + feedback, "scene", schema, maxTokens), ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                // Nothing to hand back to the model: an unreachable endpoint is not its fault.
                rejections.Add($"attempt {attempts}: the model could not answer ({ex.Message})");
                lastReasons = [];
                continue;
            }

            SceneResponse? response;
            try
            {
                response = JsonSerializer.Deserialize<SceneResponse>(raw, Json);
                if (response is not null && prose is not null)
                {
                    response = response with { Text = prose };
                }
            }
            catch (JsonException ex)
            {
                response = null;
                lastReasons = [$"The answer was not valid JSON for the schema: {ex.Message}"];
            }

            if (response is not null)
            {
                var (facts, places, reasons) = Check(response, packet, world, knownPlaces ?? [], settings);
                var choiceReasons = new List<string>();
                var choices = wantChoices ? CheckChoices(response.Choices, story, cast, choiceReasons) : [];
                reasons = [.. reasons, .. choiceReasons];

                var doubtedOnly = false;
                if (reasons.Count == 0 && settings.UseJudge)
                {
                    // Other languages have no word checks for the player's agency; the judge reads for it instead.
                    var verdict = await judge.ReviewAsync(
                        response.Text, ImmutableFacts(packet), Names(packet), agency: !NarrationLanguage.IsEnglish(packet.Language), ct: ct).ConfigureAwait(false);
                    reasons = [.. verdict.Contradictions.Select(c => $"The scene contradicts an established fact: {c}")];

                    if (verdict.PlayerActions.Count > 0)
                    {
                        doubtedOnly = verdict.Contradictions.Count == 0;
                        reasons =
                        [
                            .. reasons,
                            $"The narration decides for the player ({string.Join("; ", verdict.PlayerActions.Take(4))}). " +
                            "Never say what the player does, says, decides, thinks or feels, and give them nothing to hold; " +
                            "describe only the place, the weather and the other people.",
                        ];
                    }
                }

                // The judge's reading of agency is a second opinion that errs strict. On the last attempt its
                // doubt alone keeps the scene rather than throwing it away for the authored text.
                if (doubtedOnly && attempt == settings.MaxRetries)
                {
                    rejections.Add($"attempt {attempts}: kept despite the judge: {reasons[0]}");
                    reasons = [];
                }

                if (reasons.Count == 0)
                {
                    return new WrittenScene(
                        response.Text.Trim(),
                        response.Expression,
                        facts,
                        places,
                        Fallback: false,
                        attempts,
                        rejections,
                        string.IsNullOrWhiteSpace(response.Summary) ? null : response.Summary.Trim(),
                        [.. (response.Tags ?? []).Where(t => MemoryTags.All.Contains(t, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal)],
                        choices,
                        packet.LooseEnds is null ? null : StoryThreads.Keep(response.Threads),
                        Settled(packet, response.Resolved),
                        [.. (response.Routines ?? []).Where(r => r is not null)],
                        Outfits.Accept(packet.Outfit, response.Outfit?.Dress, response.Outfit?.Over));
                }

                lastReasons = reasons;
            }

            rejections.AddRange(lastReasons.Select(r => $"attempt {attempts}: {r}"));
        }

        return Fallback(fallbackText, attempts, rejections);
    }

    /// <summary>The loose ends an answer says it settled, among those the packet listed.</summary>
    internal static IReadOnlyList<long> Settled(ScenePacket packet, IReadOnlyList<long>? resolved) =>
        packet.LooseEnds is not { } listed
            ? []
            : [.. (resolved ?? []).Where(id => listed.Any(t => t.Id == id)).Distinct()];

    /// <summary>The answer schema, with the pack's expressions, the predicate list and the place types as enums.</summary>
    /// <param name="withText">False when the prose was written separately, and only its data is asked for.</param>
    public JsonObject Schema(ScenePacket packet, bool withText = true)
    {
        ArgumentNullException.ThrowIfNull(packet);

        static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode)JsonValue.Create(v)!)]);

        var properties = new JsonObject
        {
            ["text"] = new JsonObject { ["type"] = "string" },
            ["expression"] = new JsonObject { ["type"] = "string", ["enum"] = Strings(packet.Expressions) },
            ["facts"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["subject"] = new JsonObject { ["type"] = "string" },
                        ["predicate"] = new JsonObject { ["type"] = "string", ["enum"] = Strings(story.Predicates.Select(p => p.Id)) },
                        ["object"] = new JsonObject { ["type"] = "string" },
                        ["level"] = new JsonObject { ["type"] = "string", ["enum"] = Strings(["Established", "Claimed"]) },
                    },
                    ["required"] = Strings(["subject", "predicate", "object", "level"]),
                    ["additionalProperties"] = false,
                },
            },
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
            ["summary"] = new JsonObject { ["type"] = "string" },
            ["tags"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string", ["enum"] = Strings(MemoryTags.All) } },
            ["places"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["type"] = new JsonObject { ["type"] = "string", ["enum"] = Strings(placeTypes.All().Select(t => t.Id)) },
                        ["name"] = new JsonObject { ["type"] = "string" },
                        ["owner"] = new JsonObject { ["type"] = "string" },
                        ["look"] = new JsonObject { ["type"] = "string" },
                        // Detail ids as an enum: in other languages Gemma translated them ("кадки с растениями" for
                        // planters) even when told not to. Whether a detail fits the type is still checked below.
                        ["details"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["enum"] = Strings(placeTypes.All().SelectMany(t => t.Details ?? []).Select(d => d.Id).Distinct(StringComparer.Ordinal)),
                            },
                        },
                    },
                    ["required"] = Strings(["type", "name", "details", "owner", "look"]),
                    ["additionalProperties"] = false,
                },
            },
        };

        properties["routines"] = RoutineProperty();
        List<string> required = ["text", "expression", "facts", "places", "routines", "summary", "tags", "choices"];

        if (packet.Outfit is { Settled: false } outfit)
        {
            properties["outfit"] = OutfitProperty(outfit, nullable: false);
            required.Add("outfit");
        }

        if (packet.LooseEnds is not null)
        {
            ThreadProperties(properties);
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

    /// <summary>The routines field: what someone said about their own week. Shared with the reaction writer's schema.</summary>
    internal static JsonObject RoutineProperty() => new()
    {
        ["type"] = "array",
        ["items"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["who"] = new JsonObject { ["type"] = "string" },
                ["place"] = new JsonObject { ["type"] = "string" },
                ["slot"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("Morning", "Midday", "Afternoon", "Evening", "Night") },
                ["days"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("weekdays", "weekend", "daily") },
            },
            ["required"] = new JsonArray("who", "place", "slot", "days"),
            ["additionalProperties"] = false,
        },
    };

    /// <summary>The outfit field: a dress code among those offered, and anything worn over it. Shared with the reaction writer's schema.</summary>
    /// <param name="nullable">True for a reaction, where null means nothing changed.</param>
    internal static JsonObject OutfitProperty(PacketOutfit outfit, bool nullable) => new()
    {
        ["type"] = nullable ? new JsonArray("object", "null") : "object",
        ["properties"] = new JsonObject
        {
            ["dress"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray([.. outfit.Codes.Select(c => (JsonNode)JsonValue.Create(c)!)]) },
            ["over"] = new JsonObject { ["type"] = "string" },
        },
        ["required"] = new JsonArray("dress", "over"),
        ["additionalProperties"] = false,
    };

    /// <summary>The threads and resolved fields, shared with the reaction writer's schema.</summary>
    internal static void ThreadProperties(JsonObject properties)
    {
        properties["threads"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } };
        properties["resolved"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "integer" } };
    }

    /// <summary>Two or three distinct replies, short, each carrying at least one tag the story can score.</summary>
    /// <remarks>Shared with the reaction writer, whose answers offer the conversation's next replies.</remarks>
    internal static IReadOnlyList<ProposedChoice> CheckChoices(
        IReadOnlyList<SceneResponseChoice>? proposed,
        StoryContent story,
        Game.Core.Cast.CastContent cast,
        List<string> reasons)
    {
        var offered = proposed ?? [];

        if (offered.Count is < MinChoices or > MaxChoices)
        {
            reasons.Add($"The scene offers {offered.Count} choices; offer {MinChoices} or {MaxChoices} things the player could say or do next.");
            return [];
        }

        // Touching their want is the exception, not a default tag for any friendly line. Gemma tags most
        // friendly replies with it, and sending the scene back for that alone cost whole scenes to the
        // authored text. So C# settles it: the first reply that touches the want keeps the tag, and the others
        // lose it. A reply left with no tags stays, and simply scores nothing.
        static bool TouchesWant(string tag) =>
            tag.StartsWith(StoryContent.HelpsPrefix, StringComparison.Ordinal) || tag.StartsWith(StoryContent.HindersPrefix, StringComparison.Ordinal);

        var wantKept = false;

        var choices = new List<ProposedChoice>();
        foreach (var choice in offered)
        {
            var text = choice.Text?.Trim() ?? "";
            var tags = (choice.Tags ?? []).Distinct(StringComparer.Ordinal).ToList();

            var stripped = false;
            if (tags.Any(TouchesWant))
            {
                if (wantKept)
                {
                    tags.RemoveAll(TouchesWant);
                    stripped = true;
                }

                wantKept = true;
            }

            if (text.Length is 0 or > MaxChoiceLength)
            {
                // Left out on its own: a reply too long to show is no reason to lose the scene.
                continue;
            }

            if (choices.Any(c => string.Equals(c.Text, text, StringComparison.OrdinalIgnoreCase)))
            {
                reasons.Add($"The choice '{text}' is offered twice.");
            }
            else if ((tags.Count == 0 && !stripped) || tags.Any(t => !story.IsKnownTag(t, cast)))
            {
                reasons.Add($"The choice '{text}' needs tags from the list; it has [{string.Join(", ", tags)}].");
            }
            else
            {
                choices.Add(new ProposedChoice(text, tags));
            }
        }

        return choices;
    }

    private static WrittenScene Fallback(string text, int attempts, IReadOnlyList<string> rejections) =>
        new(text, null, [], [], Fallback: true, attempts, rejections);

    /// <summary>What the judge reads the prose against: facts that cannot change and are not mere claims.</summary>
    private IReadOnlyList<KnownFact> ImmutableFacts(ScenePacket packet) =>
        [.. packet.PlayerKnows.Concat(packet.PresentKnow)
            .Where(f => f.Fact.Level is not FactLevel.Claimed && story.Predicate(f.Fact.Predicate) is { Mutable: false })];

    private static IReadOnlyDictionary<string, string> Names(ScenePacket packet)
    {
        var names = packet.Present.ToDictionary(p => p.Id, p => p.Name, StringComparer.Ordinal);
        names[FactLedger.Player] = packet.PlayerName;
        return names;
    }

    private (IReadOnlyList<ProposedFact> Facts, IReadOnlyList<PlaceProposal> Places, IReadOnlyList<string> Reasons) Check(
        SceneResponse response,
        ScenePacket packet,
        SceneWorld world,
        IReadOnlyList<string> knownPlaces,
        LlmOptions settings)
    {
        var reasons = new List<string>();

        if (string.IsNullOrWhiteSpace(response.Text))
        {
            reasons.Add("The text is empty.");
        }
        else if (response.Text.Length > settings.MaxTextLength)
        {
            reasons.Add($"The text is {response.Text.Length} characters; keep it under {settings.MaxTextLength}.");
        }

        if (!string.IsNullOrWhiteSpace(response.Text))
        {
            // The first-person and player-action checks read English words; other languages go without.
            var english = NarrationLanguage.IsEnglish(packet.Language);

            var firstPerson = english ? Narration.FirstPersonOutsideDialogue(response.Text) : [];
            if (firstPerson.Count >= Narration.FirstPersonTolerance)
            {
                reasons.Add(
                    $"The narration slips into the first person ({string.Join(", ", firstPerson.Distinct().Take(5))}). " +
                    "Outside quoted dialogue, write only in the second person: you, your.");
            }

            var actions = english ? Narration.PlayerActions(response.Text) : [];
            if (actions.Count > 0)
            {
                reasons.Add(
                    $"The narration decides for the player ({string.Join("; ", actions.Distinct().Take(4))}). " +
                    "Never say what the player does, says, decides, thinks or feels; describe only the place, the weather and the other people.");
            }

            if (response.Text.Length > Narration.ParagraphAfter && !Narration.HasParagraphs(response.Text))
            {
                reasons.Add("The text is one block. Split it into two to four short paragraphs separated by blank lines.");
            }

            if (Narration.Unfinished(response.Text, english))
            {
                reasons.Add("The text stops mid-sentence or leaves a quote open. Finish it.");
            }
        }

        if (!packet.Expressions.Contains(response.Expression, StringComparer.OrdinalIgnoreCase))
        {
            reasons.Add($"The expression '{response.Expression}' is not one of {string.Join(", ", packet.Expressions)}.");
        }

        string[] present = [FactLedger.Player, .. packet.Present.Select(p => p.Id)];
        var facts = new List<ProposedFact>();

        foreach (var fact in response.Facts ?? [])
        {
            // A scene can show or claim things; core facts come only from the story bible.
            if (!Enum.TryParse<FactLevel>(fact.Level, out var level) || level is FactLevel.Core)
            {
                reasons.Add($"The fact '{fact.Subject} {fact.Predicate} {fact.Object}' has level '{fact.Level}'; use Established or Claimed.");
                continue;
            }

            facts.Add(new ProposedFact(
                new Fact(fact.Subject, fact.Predicate, fact.Object, level, "scene", packet.Clock.Day),
                present));
        }

        reasons.AddRange(validator.Validate(new SceneProposal(packet.Clock, packet.PlaceId, present, facts), world));

        var places = new List<PlaceProposal>();
        foreach (var place in response.Places ?? [])
        {
            var proposal = new PlaceProposal(
                place.Type, place.Name ?? "", place.Details ?? [], string.IsNullOrWhiteSpace(place.Owner) ? null : place.Owner.Trim(), place.Look);
            var problems = PlaceProposals.Check(proposal, placeTypes, [.. knownPlaces, .. places.Select(p => p.Name)]);

            if (problems.Count == 0)
            {
                places.Add(proposal with { Name = proposal.Name.Trim() });
            }
            else
            {
                reasons.AddRange(problems);
            }
        }

        return (facts, places, reasons);
    }
}
