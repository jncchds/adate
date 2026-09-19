using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Game.Core.Content;
using Game.Core.Scenes;
using Game.Core.Settings;
using Game.Core.Story;
using Microsoft.Extensions.Options;

namespace Game.Llm;

/// <summary>An authored beat whose prose the plan rewrites for the places this save got.</summary>
/// <param name="Role">The role slot it happens at, so the rewrite knows what the player is looking at.</param>
public sealed record PlannableEncounter(string Id, string Role, string Text);

public sealed record PlanPlaceEntry(string Role, string Type, string Name, IReadOnlyList<string> Details, string? Look);

public sealed record PlanEventEntry(string Id, string Name, int Day, string Role, string Time);

public sealed record PlanEncounterEntry(string Id, string Text);

public sealed record PlanResponse(
    IReadOnlyList<PlanPlaceEntry> Places,
    IReadOnlyList<PlanEventEntry> Events,
    IReadOnlyList<string> Threads,
    IReadOnlyList<PlanEncounterEntry>? Encounters);

/// <summary>
/// Lays out one save's own town (user request: the dated events and the places were the same every game,
/// so a second playthrough of a setting went the same way). The setting gives the roles a story needs
/// filled and which place types may fill each; the model decides what each one turns out to be, what is
/// dated on the calendar, what is already unfinished when the story starts, and rewrites the authored
/// beats so they read for the places it chose.
/// </summary>
/// <remarks>
/// It never writes ids, flags or anything the encounter rules read: those stay authored, because a
/// generated one that does not fire leaves a save with no way forward. Names are written in the story's
/// language; a look is English, because it reaches an image prompt.
/// </remarks>
public sealed class PlanWriter(ILlmClient llm, IOptions<LlmOptions> options)
{
    public const int MaxLookLength = 80;

    public const string SystemPrompt =
        "You lay out the town a slice-of-life dating sim takes place in, and its calendar. You are given roles to fill, " +
        "the place types each may be, and the details each type offers. Fill every role with somewhere specific and " +
        "believable that fits the setting's tone. Answer with JSON matching the schema.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static JsonObject Strings() => new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } };

    private static JsonObject Array(JsonObject properties, params string[] required) => new()
    {
        ["type"] = "array",
        ["items"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray([.. required.Select(r => (JsonNode)r)]),
            ["additionalProperties"] = false,
        },
    };

    private static readonly JsonObject Schema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["places"] = Array(
                new JsonObject
                {
                    ["role"] = new JsonObject { ["type"] = "string" },
                    ["type"] = new JsonObject { ["type"] = "string" },
                    ["name"] = new JsonObject { ["type"] = "string" },
                    ["details"] = Strings(),
                    ["look"] = new JsonObject { ["type"] = "string" },
                },
                "role", "type", "name", "details", "look"),
            ["events"] = Array(
                new JsonObject
                {
                    ["id"] = new JsonObject { ["type"] = "string" },
                    ["name"] = new JsonObject { ["type"] = "string" },
                    ["day"] = new JsonObject { ["type"] = "integer" },
                    ["role"] = new JsonObject { ["type"] = "string" },
                    ["time"] = new JsonObject { ["type"] = "string" },
                },
                "id", "name", "day", "role", "time"),
            ["threads"] = Strings(),
            ["encounters"] = Array(
                new JsonObject
                {
                    ["id"] = new JsonObject { ["type"] = "string" },
                    ["text"] = new JsonObject { ["type"] = "string" },
                },
                "id", "text"),
        },
        ["required"] = new JsonArray("places", "events", "threads", "encounters"),
        ["additionalProperties"] = false,
    };

    /// <summary>
    /// The plan for a save, or null when there is no model or nothing it answered passed, in which case
    /// the save plays the setting as it was authored.
    /// </summary>
    /// <param name="setting">The setting being planned, with its roles.</param>
    /// <param name="catalog">The place types, for what each role may be and what details it offers.</param>
    /// <param name="beats">The authored beats to rewrite; empty to leave them alone.</param>
    /// <param name="language">The language the story is written in. Names come back in it.</param>
    public async Task<SavePlan?> WriteAsync(
        SettingDefinition setting,
        ILocationCatalog catalog,
        IReadOnlyList<PlannableEncounter> beats,
        string? language,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(setting);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(beats);

        var settings = options.Value;
        if (!settings.Enabled)
        {
            return null;
        }

        var request = Request(setting, catalog, beats, language);
        IReadOnlyList<string> lastReasons = [];

        for (var attempt = 0; attempt <= settings.MaxRetries; attempt++)
        {
            var user = lastReasons.Count == 0
                ? request
                : request + "\n\n## Your previous answer was rejected\n" + string.Join("\n", lastReasons.Select(r => "- " + r));

            PlanResponse? response;
            try
            {
                var raw = await llm.CompleteJsonAsync(new LlmRequest(SystemPrompt, user, "plan", Schema, 4000), ct).ConfigureAwait(false);
                response = JsonSerializer.Deserialize<PlanResponse>(raw, Json);
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

            if (response is null)
            {
                lastReasons = ["The answer was empty."];
                continue;
            }

            var (plan, reasons) = Read(response, setting, beats);
            if (plan is not null && reasons.Count == 0)
            {
                var checks = SavePlans.Check(plan, setting, catalog);
                if (checks.Count == 0)
                {
                    return plan;
                }

                reasons = checks;
            }

            lastReasons = reasons;
        }

        return null;
    }

    /// <summary>The answer as a plan, with the reasons it cannot be read: a bad time of day is the model's to fix.</summary>
    private static (SavePlan? Plan, IReadOnlyList<string> Reasons) Read(
        PlanResponse response, SettingDefinition setting, IReadOnlyList<PlannableEncounter> beats)
    {
        var reasons = new List<string>();
        var events = new List<PlannedEvent>();

        foreach (var entry in response.Events ?? [])
        {
            if (!Enum.TryParse<TimeOfDay>(entry.Time?.Trim(), ignoreCase: true, out var time) || !Enum.IsDefined(time))
            {
                reasons.Add($"Event '{entry.Id}' happens at '{entry.Time}'; use one of {string.Join(", ", Enum.GetNames<TimeOfDay>())}.");
                continue;
            }

            events.Add(new PlannedEvent(entry.Id?.Trim() ?? "", entry.Name?.Trim() ?? "", entry.Day, entry.Role?.Trim() ?? "", time));
        }

        var wanted = beats.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var entry in response.Encounters ?? [])
        {
            if (entry.Id is not { } id || !wanted.Contains(id) || string.IsNullOrWhiteSpace(entry.Text))
            {
                continue;
            }

            // A beat's text is filled in before it is shown, and a rewrite that drops a token loses whoever
            // or wherever it named. Refused rather than repaired: the model can put it back in a sentence.
            var original = beats.First(b => b.Id == id).Text;
            var missing = Tokens(original).Except(Tokens(entry.Text), StringComparer.Ordinal).ToList();

            if (missing.Count > 0)
            {
                reasons.Add($"The rewrite of '{id}' leaves out {string.Join(" and ", missing.Select(t => $"{{{t}}}"))}, which the beat needs. Keep every {{...}} exactly as written.");
                continue;
            }

            texts[id] = entry.Text.Trim();
        }

        foreach (var missing in wanted.Where(id => !texts.ContainsKey(id)).Order(StringComparer.Ordinal))
        {
            reasons.Add($"Beat '{missing}' was not rewritten. Every beat given needs its text.");
        }

        var plan = new SavePlan(
            [
                .. (response.Places ?? []).Select(p => new PlannedPlace(
                    p.Role?.Trim() ?? "", p.Type?.Trim() ?? "", p.Name?.Trim() ?? "", [.. p.Details ?? []], p.Look?.Trim())),
            ],
            events,
            [.. (response.Threads ?? []).Select(t => t?.Trim() ?? "").Where(t => t.Length > 0)],
            texts);

        return (plan, reasons);
    }

    /// <summary>The words an encounter's text asks to have filled in, such as <c>main_li</c> in "{main_li} looks up".</summary>
    private static IEnumerable<string> Tokens(string text) =>
        System.Text.RegularExpressions.Regex.Matches(text, @"\{(\w+)\}").Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal);

    private static string Request(
        SettingDefinition setting, ILocationCatalog catalog, IReadOnlyList<PlannableEncounter> beats, string? language)
    {
        var text = new StringBuilder();
        text.AppendLine($"## The setting\n{setting.DisplayName}. {setting.Tone}");
        text.AppendLine($"\nThe story runs {setting.Days} days.");

        text.AppendLine("\n## Roles to fill\nFill every one. Give each a place type from the ones listed for it, and up to " +
            $"{Game.Core.Places.PlaceProposals.MaxDetails} of that type's details.");

        foreach (var role in setting.Places)
        {
            text.AppendLine($"\n- **{role.Id}**{(role.Known ? "" : " (found later in the story, not known from the start)")}{Role(setting, role)}");

            foreach (var type in SavePlans.TypesFor(role))
            {
                var definition = catalog.Get(type);
                var details = (definition.Details ?? []).Select(d => $"{d.Id} ({d.Phrase})");
                text.AppendLine($"  - `{type}`: {definition.DisplayName}. Details: {(details.Any() ? string.Join("; ", details) : "none")}");
            }
        }

        text.AppendLine($"""

            ## What to write

            ### places
            One per role, with:
            - `role`: the role id, exactly as listed.
            - `type`: one of the place types offered for that role.
            - `name`: what people here call it, 1 to {Game.Core.Places.PlaceProposals.MaxNameLength} characters. Specific and local, not "The Cafe": somewhere with an owner, a history or a joke behind the name. Every place needs its own name.
            - `details`: detail ids of the type you chose, or an empty list.
            - `look`: a few short visual phrases in English for how this one looks, under {MaxLookLength} characters, such as "converted church, stained glass, mismatched chairs". It is drawn, never shown to the player. An empty string leaves it to the type.

            ### events
            {SavePlans.MinEvents} to {SavePlans.MaxEvents} dated things the whole town turns up to, each on its own day between day {SavePlans.EventMargin} and day {setting.Days - SavePlans.EventMargin}, spread across the story with the largest late. Each has:
            - `id`: lower-case English words joined by dashes, such as `harvest-market`.
            - `name`: what it is called, under {SavePlans.MaxEventNameLength} characters.
            - `day`: which day it falls on.
            - `role`: the role id of where it happens.
            - `time`: one of {string.Join(", ", Enum.GetNames<TimeOfDay>())}.

            ### threads
            Up to {SavePlans.MaxThreads} things already unfinished in this town as the story opens: a question hanging over it, something everyone is waiting on, a decision someone has to make. One short sentence each, under {SavePlans.MaxThreadLength} characters. They are about the town, not about anyone the player has not met.
            """);

        if (beats.Count > 0)
        {
            text.AppendLine("""

                ### encounters
                Small moments already written for this story, each at one of the roles. Rewrite the text of every one so it
                reads for the place you just decided that role is: keep exactly what happens and what it tells the player,
                change only the surroundings and the particulars. Same length, same tense. Answer with every id given.
                """);

            foreach (var beat in beats)
            {
                text.AppendLine($"- `{beat.Id}` at **{beat.Role}**: {beat.Text}");
            }
        }

        if (!NarrationLanguage.IsEnglish(language))
        {
            text.AppendLine($"""

                ## Language
                Write every `name`, every event name, every thread and every rewritten encounter text in {language}, and
                name places as they would really be named in {language}, not translated from English.
                Keep everything else exactly as listed, in English: every `role`, `type`, `detail`, `id`, `time` and `look`.
                """);
        }

        return text.ToString();
    }

    /// <summary>What a role is for, so the place chosen can carry it: where the player lives, works, or first meets someone.</summary>
    private static string Role(SettingDefinition setting, SettingPlace role)
    {
        List<string> parts = [];

        if (string.Equals(setting.Home ?? setting.RoutinePlace, role.Id, StringComparison.Ordinal))
        {
            parts.Add("where the player lives");
        }

        if (string.Equals(setting.RoutinePlace, role.Id, StringComparison.Ordinal))
        {
            parts.Add("where the player passes through most days");
        }

        if (setting.Job is { } job && string.Equals(job.Place, role.Id, StringComparison.Ordinal))
        {
            parts.Add($"where the player works, as {job.Title.Replace("{place}", "this place", StringComparison.Ordinal)}");
        }

        if (setting.Openings.Any(o => string.Equals(o.MeetingPlace, role.Id, StringComparison.Ordinal)))
        {
            parts.Add("somewhere the player could first meet someone");
        }

        return parts.Count == 0 ? "" : ": " + string.Join(", ", parts);
    }
}
