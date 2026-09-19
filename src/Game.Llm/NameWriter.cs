using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Game.Core.Story;
using Microsoft.Extensions.Options;

namespace Game.Llm;

public sealed record NamesResponse(IReadOnlyList<string> Names);

/// <summary>
/// Names the people a player can fall for, in the language the story is written in (user request: a
/// Russian story introduced Aya, Hana and Mei). The shipped pool is English, and a name is the one
/// piece of a character the player reads on every line, so a story in another language needs names
/// that belong to it rather than transliterations of someone else's.
/// </summary>
/// <remarks>
/// Only the cast is named this way. The player's own name and the main love interest's are typed when
/// the game is started, and are left exactly as they were typed. Without a model, or when nothing
/// answered passes, the caller falls back to the authored pool, which is why this returns what it got
/// rather than throwing.
/// </remarks>
public sealed partial class NameWriter(ILlmClient llm, IOptions<LlmOptions> options)
{
    public const int MaxLength = 24;

    public const string SystemPrompt =
        "You name the characters of a slice-of-life dating sim. Answer with JSON matching the schema.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly JsonObject Schema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["names"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
        },
        ["required"] = new JsonArray("names"),
        ["additionalProperties"] = false,
    };

    /// <summary>
    /// Up to <paramref name="count"/> given names, none of them one of <paramref name="taken"/>; fewer,
    /// or none at all, when there is no model or the answer did not hold that many.
    /// </summary>
    /// <param name="subject">female, male or another subject the style pack draws; names suit it.</param>
    /// <param name="taken">Names already in this story, which no one else may share.</param>
    /// <param name="language">The language the story is written in; null or English for English.</param>
    public async Task<IReadOnlyList<string>> WriteAsync(
        string subject,
        int count,
        IEnumerable<string> taken,
        string settingName,
        string tone,
        string? language,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(taken);

        var settings = options.Value;
        if (!settings.Enabled || count <= 0)
        {
            return [];
        }

        var already = taken.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        var spoken = NarrationLanguage.IsEnglish(language) ? NarrationLanguage.Default : language!;

        var request = $"""
            ## Setting
            {settingName}. {tone}

            ## What to write
            {count} given names for {subject} characters who live here, as JSON under `names`.

            ## Rules
            - Write them in {spoken}, in the script {spoken} is written in: names people in this setting would really have, not translations of English ones.
            - Given names only, no surnames, no titles, each under {MaxLength} characters.
            - All different from each other{(already.Count == 0 ? "" : $", and none of these, which are taken: {string.Join(", ", already)}")}.
            - Ordinary names for ordinary people. Do not make them all unusual.
            """;

        try
        {
            var raw = await llm.CompleteJsonAsync(new LlmRequest(SystemPrompt, request, "names", Schema, 500), ct).ConfigureAwait(false);
            var seen = new HashSet<string>(already, StringComparer.OrdinalIgnoreCase);

            return
            [
                .. (JsonSerializer.Deserialize<NamesResponse>(raw, Json)?.Names ?? [])
                    .Select(n => n?.Trim() ?? "")
                    .Where(n => n.Length is > 0 and <= MaxLength && Name().IsMatch(n) && seen.Add(n))
                    .Take(count),
            ];
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or InvalidOperationException
                                   || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return [];
        }
    }

    /// <summary>A name in any script: letters and marks, with spaces, hyphens and apostrophes inside it.</summary>
    [GeneratedRegex(@"^[\p{L}\p{M}][\p{L}\p{M} '\-]*$")]
    private static partial Regex Name();
}
