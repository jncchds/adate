using System.Text.Json;
using System.Text.Json.Nodes;
using Game.Core.Story;
using Microsoft.Extensions.Options;

namespace Game.Llm;

/// <summary>Someone the story bible is written for. C# has already picked their job and want (plan §7).</summary>
public sealed record BiblePerson(string Id, string Name, string WorksAs, string Want, IReadOnlyList<string> Temper);

public sealed record BibleEntry(string Id, IReadOnlyList<string> Likes, string Secret);

public sealed record BibleResponse(IReadOnlyList<BibleEntry> People);

/// <summary>
/// Story bible flavour (plan §7): C# picks each person's job and want; the model adds what they like
/// and one secret, as schema-checked JSON. Accepted details become <c>core</c> facts; if no answer
/// passes, the story runs on the C# picks alone.
/// </summary>
public sealed class BibleWriter(ILlmClient llm, IOptions<LlmOptions> options)
{
    public const int MaxLikes = 3;
    public const int MaxLikeLength = 60;
    public const int MaxSecretLength = 140;
    public const string Source = "bible";

    public const string SystemPrompt =
        "You add small, specific, believable details to the people in a slice-of-life dating sim. " +
        "Stay true to each person's job, want and temper. Answer with JSON matching the schema.";

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
                        ["likes"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                        ["secret"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray("id", "likes", "secret"),
                    ["additionalProperties"] = false,
                },
            },
        },
        ["required"] = new JsonArray("people"),
        ["additionalProperties"] = false,
    };

    /// <summary>Core facts for everyone given, or none when there is no model or no answer passed.</summary>
    public async Task<IReadOnlyList<Fact>> WriteAsync(string settingName, string tone, IReadOnlyList<BiblePerson> people, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(people);

        var settings = options.Value;
        if (!settings.Enabled || people.Count == 0)
        {
            return [];
        }

        var request = $"## Setting\n{settingName}. {tone}\n\n## People\n" + string.Join("\n", people.Select(p =>
            $"- id {p.Id}: {p.Name}, works as {p.WorksAs}, wants to {p.Want}. {string.Join(" ", p.Temper)}")) +
            $"\n\n## Rules\n- For every person: one to {MaxLikes} likes, each under {MaxLikeLength} characters.\n" +
            $"- One secret each, under {MaxSecretLength} characters, something they would not say on a first meeting.\n" +
            "- Use exactly the ids given, once each.\n" +
            "- Write likes as short noun phrases and the secret in the third person about them, for example \"Still keeps every letter from an estranged sibling.\"";

        IReadOnlyList<string> lastReasons = [];
        for (var attempt = 0; attempt <= settings.MaxRetries; attempt++)
        {
            var user = lastReasons.Count == 0
                ? request
                : request + "\n\n## Your previous answer was rejected\n" + string.Join("\n", lastReasons.Select(r => "- " + r));

            BibleResponse? response;
            try
            {
                var raw = await llm.CompleteJsonAsync(new LlmRequest(SystemPrompt, user, "bible", Schema), ct).ConfigureAwait(false);
                response = JsonSerializer.Deserialize<BibleResponse>(raw, Json);
            }
            catch (JsonException ex)
            {
                lastReasons = [$"The answer was not valid JSON for the schema: {ex.Message}"];
                continue;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                lastReasons = [];
                continue;
            }

            var reasons = Check(response, people);
            if (reasons.Count == 0)
            {
                return
                [
                    .. response!.People.SelectMany(entry => entry.Likes
                        .Select(like => new Fact(entry.Id, "likes", like.Trim(), FactLevel.Core, Source, 0))
                        .Append(new Fact(entry.Id, "has-secret", entry.Secret.Trim(), FactLevel.Core, Source, 0))),
                ];
            }

            lastReasons = reasons;
        }

        return [];
    }

    private static IReadOnlyList<string> Check(BibleResponse? response, IReadOnlyList<BiblePerson> people)
    {
        if (response?.People is null)
        {
            return ["The answer has no people."];
        }

        var reasons = new List<string>();
        var ids = people.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in response.People)
        {
            if (!ids.Contains(entry.Id) || !seen.Add(entry.Id))
            {
                reasons.Add($"'{entry.Id}' is not one of the ids given, or appears twice.");
                continue;
            }

            var likes = entry.Likes ?? [];
            if (likes.Count is 0 or > MaxLikes || likes.Any(l => string.IsNullOrWhiteSpace(l) || l.Length > MaxLikeLength))
            {
                reasons.Add($"'{entry.Id}' needs one to {MaxLikes} likes, each under {MaxLikeLength} characters.");
            }

            if (string.IsNullOrWhiteSpace(entry.Secret) || entry.Secret.Length > MaxSecretLength)
            {
                reasons.Add($"'{entry.Id}' needs one secret under {MaxSecretLength} characters.");
            }
        }

        foreach (var missing in ids.Except(seen))
        {
            reasons.Add($"'{missing}' is missing.");
        }

        return reasons;
    }
}
