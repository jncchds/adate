using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Game.Core;
using Game.Core.Story;
using Microsoft.Extensions.Options;

namespace Game.Llm;

/// <summary>Everything the epilogue may draw on. C# decided the ending; the model only tells it.</summary>
/// <param name="PartnerTemper">How the partner is, as the scene packets describe them.</param>
/// <param name="Memories">What happened, oldest first, as the memory summaries tell it.</param>
/// <param name="Language">The language the story is written in; null or English for English.</param>
/// <param name="PlayerGender">For grammatical gender, in languages that mark it.</param>
public sealed record EpilogueRequest(
    string Setting,
    string Tone,
    string Player,
    EndingKind Kind,
    string? Partner,
    IReadOnlyList<string> PartnerTemper,
    IReadOnlyList<string> PassedOver,
    IReadOnlyList<string> Departures,
    IReadOnlyList<string> Memories,
    IReadOnlyList<RecapLine> Choices,
    Ceiling Ceiling,
    string? Language = null,
    string? PlayerGender = null);

public sealed record WrittenEpilogue(string Text, bool Fallback, int Attempts, IReadOnlyList<string> Rejections);

internal sealed record EpilogueResponse(string Text);

/// <summary>
/// Writes the ending (phase-3 plan: Gemma writes it). Checked like a scene: second person, finished,
/// not too long, and naming the partner when the story ends together; the authored text otherwise.
/// </summary>
public sealed class EpilogueWriter(ILlmClient llm, IOptions<LlmOptions> options)
{
    public const int MaxLength = 2200;
    public const int MaxTokens = 1000;

    public const string SystemPrompt =
        "You write the closing pages of a first-person dating sim, addressed to the player as \"you\". " +
        "The ending has already been decided; tell it warmly and specifically. Answer with JSON matching the schema.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly JsonObject Schema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["text"] = new JsonObject { ["type"] = "string" } },
        ["required"] = new JsonArray("text"),
        ["additionalProperties"] = false,
    };

    public async Task<WrittenEpilogue> WriteAsync(EpilogueRequest request, string fallbackText, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var settings = options.Value;
        if (!settings.Enabled)
        {
            return new WrittenEpilogue(fallbackText, Fallback: true, Attempts: 0, []);
        }

        var prompt = Render(request);
        var rejections = new List<string>();
        IReadOnlyList<string> lastReasons = [];
        var attempts = 0;

        for (var attempt = 0; attempt <= settings.MaxRetries; attempt++)
        {
            attempts++;
            var user = lastReasons.Count == 0
                ? prompt
                : prompt + "\n\n## Your previous answer was rejected\n" + string.Join("\n", lastReasons.Select(r => "- " + r));

            EpilogueResponse? response;
            try
            {
                var raw = await llm.CompleteJsonAsync(new LlmRequest(SystemPrompt, user, "epilogue", Schema, MaxTokens), ct).ConfigureAwait(false);
                response = JsonSerializer.Deserialize<EpilogueResponse>(raw, Json);
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

            var reasons = Check(response?.Text, request);
            if (reasons.Count == 0)
            {
                return new WrittenEpilogue(response!.Text.Trim(), Fallback: false, attempts, rejections);
            }

            lastReasons = reasons;
            rejections.AddRange(reasons.Select(r => $"attempt {attempts}: {r}"));
        }

        return new WrittenEpilogue(fallbackText, Fallback: true, attempts, rejections);
    }

    public static string Render(EpilogueRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var text = new StringBuilder();
        text.AppendLine($"## Story\n{request.Setting}. {request.Tone}");
        text.AppendLine($"The player is {request.Player}. Content ceiling: {request.Ceiling}.");

        text.AppendLine("\n## How it ends");
        text.AppendLine(request.Kind switch
        {
            EndingKind.Together => $"The player chose to stay with {request.Partner}.",
            EndingKind.Alone => "The player chose to leave on their own, though someone was still waiting for an answer.",
            _ => "No one was left waiting: the people the player grew close to walked away.",
        });

        if (request.Partner is not null && request.PartnerTemper.Count > 0)
        {
            text.AppendLine($"{request.Partner}: {string.Join(" ", request.PartnerTemper)}");
        }

        if (request.PassedOver.Count > 0)
        {
            text.AppendLine($"Also still around, not chosen: {string.Join(", ", request.PassedOver)}.");
        }

        foreach (var departure in request.Departures)
        {
            text.AppendLine($"- {departure}");
        }

        if (request.Memories.Count > 0)
        {
            text.AppendLine("\n## What happened");
            foreach (var memory in request.Memories)
            {
                text.AppendLine($"- {memory}");
            }
        }

        if (request.Choices.Count > 0)
        {
            text.AppendLine("\n## Choices that mattered");
            foreach (var line in request.Choices)
            {
                text.AppendLine($"- Day {line.Day}: \"{line.Words}\" ({string.Join("; ", line.Influence)})");
            }
        }

        text.AppendLine("\n## Write");
        text.AppendLine("- text: two or three short paragraphs separated by blank lines, a little while after the last day, in the second person.");
        text.AppendLine("- Recall one or two concrete moments or choices from above. Invent no new people and no events that contradict them.");
        text.AppendLine("- Keep it within the content ceiling. End on a finished sentence.");
        foreach (var rule in NarrationLanguage.WritingRules(request.Language, request.PlayerGender))
        {
            text.AppendLine($"- {rule}");
        }

        return text.ToString();
    }

    private static List<string> Check(string? text, EpilogueRequest request)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ["The epilogue is empty."];
        }

        var reasons = new List<string>();

        if (text.Length > MaxLength)
        {
            reasons.Add($"The epilogue is {text.Length} characters; keep it under {MaxLength}.");
        }

        // First person and the partner's name are checked in English only: other languages have their own
        // pronouns, and may inflect or transliterate a name.
        var english = NarrationLanguage.IsEnglish(request.Language);

        var firstPerson = english ? Narration.FirstPersonOutsideDialogue(text) : [];
        if (firstPerson.Count >= Narration.FirstPersonTolerance)
        {
            reasons.Add($"The narration slips into the first person ({string.Join(", ", firstPerson.Distinct().Take(5))}). Write in the second person.");
        }

        if (Narration.Unfinished(text, english))
        {
            reasons.Add("The epilogue stops mid-sentence or leaves a quote open. Finish it.");
        }

        if (english && request.Kind is EndingKind.Together && request.Partner is { } partner && !text.Contains(partner, StringComparison.Ordinal))
        {
            reasons.Add($"The story ends with {partner}; name them.");
        }

        return reasons;
    }
}
