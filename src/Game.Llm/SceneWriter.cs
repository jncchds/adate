using System.Text.Json;
using System.Text.Json.Nodes;
using Game.Core.Story;
using Microsoft.Extensions.Options;

namespace Game.Llm;

public sealed record SceneResponseFact(string Subject, string Predicate, string Object, string Level);

/// <summary>The JSON half of a written scene.</summary>
public sealed record SceneResponse(string Text, string Expression, IReadOnlyList<SceneResponseFact>? Facts);

/// <param name="Fallback">Whether the authored text was used because no answer passed.</param>
/// <param name="Rejections">Why each rejected attempt failed, in order; kept for the turn log.</param>
public sealed record WrittenScene(
    string Text,
    string? Expression,
    IReadOnlyList<ProposedFact> Facts,
    bool Fallback,
    int Attempts,
    IReadOnlyList<string> Rejections);

/// <summary>
/// Writes one scene (plan §8). C# assembles the packet; the model returns prose plus JSON; C# checks
/// it against the state it owns and retries with the reasons. After the retries run out, the
/// encounter's authored text is used, so the game never waits on or trusts a bad answer.
/// </summary>
public sealed class SceneWriter(ILlmClient llm, SceneValidator validator, StoryContent story, IOptions<LlmOptions> options)
{
    public const string SystemPrompt =
        "You write single scenes for a first-person dating sim. You are given the situation, who is " +
        "present, what each of them knows, and what must happen. Write only that scene, stay inside " +
        "what you are told, and answer with JSON matching the schema.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<WrittenScene> WriteAsync(ScenePacket packet, SceneWorld world, string fallbackText, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(world);

        var settings = options.Value;
        if (!settings.Enabled)
        {
            return new WrittenScene(fallbackText, null, [], Fallback: true, Attempts: 0, []);
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
                var (facts, reasons) = Check(response, packet, world, settings);
                if (reasons.Count == 0)
                {
                    return new WrittenScene(response.Text.Trim(), response.Expression, facts, Fallback: false, attempts, rejections);
                }

                lastReasons = reasons;
            }

            rejections.AddRange(lastReasons.Select(r => $"attempt {attempts}: {r}"));
        }

        return new WrittenScene(fallbackText, null, [], Fallback: true, attempts, rejections);
    }

    /// <summary>The answer schema, with the pack's expressions and the predicate list as enums.</summary>
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
            },
            ["required"] = Strings(["text", "expression", "facts"]),
            ["additionalProperties"] = false,
        };
    }

    private (IReadOnlyList<ProposedFact> Facts, IReadOnlyList<string> Reasons) Check(
        SceneResponse response,
        ScenePacket packet,
        SceneWorld world,
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

        return (facts, reasons);
    }
}
