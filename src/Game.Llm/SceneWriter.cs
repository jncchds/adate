using System.Text.Json;
using System.Text.Json.Nodes;
using Game.Core.Content;
using Game.Core.Places;
using Game.Core.Story;
using Microsoft.Extensions.Options;

namespace Game.Llm;

public sealed record SceneResponseFact(string Subject, string Predicate, string Object, string Level);

public sealed record SceneResponsePlace(string Type, string Name, IReadOnlyList<string>? Details);

/// <summary>The JSON half of a written scene.</summary>
public sealed record SceneResponse(
    string Text,
    string Expression,
    IReadOnlyList<SceneResponseFact>? Facts,
    IReadOnlyList<SceneResponsePlace>? Places = null);

/// <param name="Places">New places the scene named, checked against the place-type catalog.</param>
/// <param name="Fallback">Whether the authored text was used because no answer passed.</param>
/// <param name="Rejections">Why each rejected attempt failed, in order; kept for the turn log.</param>
public sealed record WrittenScene(
    string Text,
    string? Expression,
    IReadOnlyList<ProposedFact> Facts,
    IReadOnlyList<PlaceProposal> Places,
    bool Fallback,
    int Attempts,
    IReadOnlyList<string> Rejections);

/// <summary>
/// Writes one scene (plan §8). C# assembles the packet; the model returns prose plus JSON; C# checks
/// it against the state it owns, optionally asks the judge about the prose, and retries with the
/// reasons. After the retries run out, the encounter's authored text is used, so the game never waits
/// on or trusts a bad answer.
/// </summary>
public sealed class SceneWriter(
    ILlmClient llm,
    SceneValidator validator,
    StoryContent story,
    ILocationCatalog placeTypes,
    SceneJudge judge,
    IOptions<LlmOptions> options)
{
    public const string SystemPrompt =
        "You write single scenes for a first-person dating sim. You are given the situation, who is " +
        "present, what each of them knows, and what must happen. Write only that scene, stay inside " +
        "what you are told, and answer with JSON matching the schema.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <param name="knownPlaces">Places the player knows. A proposal may not repeat one of their names.</param>
    public async Task<WrittenScene> WriteAsync(
        ScenePacket packet,
        SceneWorld world,
        string fallbackText,
        IReadOnlyList<string>? knownPlaces = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(world);

        var settings = options.Value;
        if (!settings.Enabled)
        {
            return Fallback(fallbackText, 0, []);
        }

        var request = ScenePacketBuilder.Render(packet);
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

            string raw;
            try
            {
                raw = await llm.CompleteJsonAsync(new LlmRequest(SystemPrompt, user, "scene", schema), ct).ConfigureAwait(false);
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
            }
            catch (JsonException ex)
            {
                response = null;
                lastReasons = [$"The answer was not valid JSON for the schema: {ex.Message}"];
            }

            if (response is not null)
            {
                var (facts, places, reasons) = Check(response, packet, world, knownPlaces ?? [], settings);

                if (reasons.Count == 0 && settings.UseJudge)
                {
                    var contradictions = await judge.CheckAsync(response.Text, ImmutableFacts(packet), Names(packet), ct).ConfigureAwait(false);
                    reasons = [.. contradictions.Select(c => $"The scene contradicts an established fact: {c}")];
                }

                if (reasons.Count == 0)
                {
                    return new WrittenScene(response.Text.Trim(), response.Expression, facts, places, Fallback: false, attempts, rejections);
                }

                lastReasons = reasons;
            }

            rejections.AddRange(lastReasons.Select(r => $"attempt {attempts}: {r}"));
        }

        return Fallback(fallbackText, attempts, rejections);
    }

    /// <summary>The answer schema, with the pack's expressions, the predicate list and the place types as enums.</summary>
    public JsonObject Schema(ScenePacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode)JsonValue.Create(v)!)]);

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
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
                            ["details"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                        },
                        ["required"] = Strings(["type", "name", "details"]),
                        ["additionalProperties"] = false,
                    },
                },
            },
            ["required"] = Strings(["text", "expression", "facts", "places"]),
            ["additionalProperties"] = false,
        };
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
            var proposal = new PlaceProposal(place.Type, place.Name ?? "", place.Details ?? []);
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
