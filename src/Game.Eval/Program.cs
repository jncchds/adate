using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Game.Core.Saves;
using Game.Core.Story;
using Game.Data;
using Game.Llm;
using Game.Play;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

// Measures ways of writing instead of guessing (user request: raise the quality of the content). Past scenes from a
// copy of a real database are written again with each variant, on whichever model is named, and a judge model
// scores the versions of each scene side by side.
//
//   dotnet run --project src/Game.Eval -- cases [--db <adate.db>] [--count 10]
//   dotnet run --project src/Game.Eval -- run --variant baseline|twopass|material|full|voices|happenings|threads|varied [--model <id>]
//   dotnet run --project src/Game.Eval -- grade --judge <model id> [--runs label,label]
//
// Everything lands in eval/llm/out: the working database copy, cases.json, runs/*.json, grades.json and report.md.

var root = RepoRoot();
var outDir = Path.Combine(root, "eval", "llm", "out");
var runsDir = Path.Combine(outDir, "runs");
var workDb = Path.Combine(outDir, "work.db");
var casesFile = Path.Combine(outDir, "cases.json");
Directory.CreateDirectory(runsDir);

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };

return (args.FirstOrDefault() ?? "help") switch
{
    "cases" => await CasesAsync(),
    "run" => await RunAsync(),
    "grade" => await GradeAsync(),
    _ => Help(),
};

async Task<int> CasesAsync()
{
    var source = Arg("--db") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "adate", "adate.db");
    var count = int.Parse(Arg("--count") ?? "10", CultureInfo.InvariantCulture);

    // The backup API copies a live database with its write-ahead log, which a file copy would miss.
    SqliteConnection.ClearAllPools();
    File.Delete(workDb);
    using (var from = new SqliteConnection($"Data Source={source};Mode=ReadOnly"))
    using (var to = new SqliteConnection($"Data Source={workDb}"))
    {
        from.Open();
        to.Open();
        from.BackupDatabase(to);
    }

    SqliteConnection.ClearAllPools();

    var rows = new List<(string Save, long Id, string Encounter, int Replies)>();
    using (var connection = new SqliteConnection($"Data Source={workDb}"))
    {
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT save_id, id, encounter_id, COALESCE(json_array_length(exchanges_json), 0)
            FROM scene_log WHERE written = 1 AND encounter_id IS NOT NULL ORDER BY id;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt32(3)));
        }
    }

    // A spread of kinds of scene, the newest of each first and those with a reply before those without.
    var queues = rows
        .GroupBy(r => Kind(r.Encounter))
        .Select(g => new Queue<(string Save, long Id, string Encounter, int Replies)>(g.OrderByDescending(r => r.Replies > 0).ThenByDescending(r => r.Id)))
        .ToList();
    var picked = new List<(string Save, long Id, string Encounter, int Replies)>();
    while (picked.Count < count && queues.Any(q => q.Count > 0))
    {
        foreach (var queue in queues.Where(q => q.Count > 0))
        {
            if (picked.Count < count)
            {
                picked.Add(queue.Dequeue());
            }
        }
    }

    await using var provider = Services(Variant("full"));
    var world = provider.GetRequiredService<WorldService>();
    var cases = new List<EvalCase>();
    foreach (var row in picked)
    {
        Console.WriteLine($"case: scene {row.Id} ({row.Encounter}, {row.Replies} replies)");
        var saveId = SaveId.Parse(row.Save);
        await world.PrepareReplayAsync(saveId);
        cases.Add(new EvalCase(row.Save, row.Id, row.Encounter, row.Replies > 0, await world.LooseEndsBeforeAsync(saveId, row.Id)));
    }

    await File.WriteAllTextAsync(casesFile, JsonSerializer.Serialize(cases, json));
    Console.WriteLine($"{cases.Count} cases written to {casesFile}");
    return 0;
}

async Task<int> RunAsync()
{
    var variant = Arg("--variant") ?? "baseline";
    var cases = JsonSerializer.Deserialize<List<EvalCase>>(await File.ReadAllTextAsync(casesFile), json)
        ?? throw new InvalidOperationException("No cases; run 'cases' first.");

    var overrides = Variant(variant);
    if (Arg("--model") is { } model)
    {
        overrides["Llm:Model"] = model;
    }

    await using var provider = Services(overrides);
    var options = provider.GetRequiredService<IOptions<LlmOptions>>().Value;
    var label = Arg("--name") ?? $"{variant}@{options.Model[(options.Model.LastIndexOf('/') + 1)..]}";
    var world = provider.GetRequiredService<WorldService>();

    var outputs = new List<EvalOutput>();
    foreach (var c in cases)
    {
        var saveId = SaveId.Parse(c.SaveId);

        Console.WriteLine($"{label}: scene {c.SceneId} ({c.Encounter})");
        var scene = await world.ReplaySceneAsync(saveId, c.SceneId, c.LooseEnds);
        outputs.Add(new EvalOutput(
            c.SceneId, "scene", c.Encounter, scene.Packet, null, null, scene.Written.Text,
            [.. (scene.Written.Choices ?? []).Select(x => x.Text)], scene.Written.Fallback, scene.Written.Attempts,
            scene.Written.Rejections, scene.Seconds, scene.Written.Threads));

        if (c.HasReply && await world.ReplayReactionAsync(saveId, c.SceneId, c.LooseEnds) is { } reaction)
        {
            Console.WriteLine($"{label}: reaction to scene {c.SceneId}");
            outputs.Add(new EvalOutput(
                c.SceneId, "reaction", c.Encounter, reaction.Packet, reaction.SceneText, reaction.Reply, reaction.Written.Text,
                [.. (reaction.Written.Choices ?? []).Select(x => x.Text)], reaction.Written.Fallback, reaction.Written.Attempts,
                reaction.Written.Rejections, reaction.Seconds, reaction.Written.Threads));
        }
    }

    var file = Path.Combine(runsDir, string.Concat(label.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '.' ? ch : '_')) + ".json");
    await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new EvalRun(label, variant, options.Model, outputs), json));
    Console.WriteLine($"{outputs.Count} outputs written to {file}");
    return 0;
}

async Task<int> GradeAsync()
{
    var judgeModel = Arg("--judge") ?? throw new InvalidOperationException("Name the judge model with --judge.");
    var only = Arg("--runs")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    var runs = Directory.GetFiles(runsDir, "*.json")
        .Select(f => JsonSerializer.Deserialize<EvalRun>(File.ReadAllText(f), json)!)
        .Where(r => only is null || only.Contains(r.Label))
        .OrderBy(r => r.Model, StringComparer.Ordinal)
        .ThenBy(r => VariantOrder(r.Variant))
        .ToList();

    await using var provider = Services(new Dictionary<string, string?>
    {
        ["Llm:Model"] = judgeModel,
        ["Llm:Temperature"] = "0.2",
        ["Llm:RequestTimeout"] = "00:15:00",
    });
    var llm = provider.GetRequiredService<ILlmClient>();

    var grades = new List<EvalGrade>();
    var keys = runs.SelectMany(r => r.Outputs.Select(o => (o.SceneId, o.Kind))).Distinct().ToList();
    foreach (var (sceneId, kind) in keys)
    {
        var entries = runs
            .Select(r => (Run: r, Output: r.Outputs.FirstOrDefault(o => o.SceneId == sceneId && o.Kind == kind)))
            .Where(e => e.Output is not null)
            .Select(e => (e.Run, Output: e.Output!))
            .ToList();

        // The situation as the plainest writer was given it; material the others had is theirs to use well.
        var context = entries.FirstOrDefault(e => e.Run.Variant == "baseline").Output ?? entries[0].Output;

        foreach (var chunk in entries.GroupBy(e => e.Run.Model).SelectMany(g => g.Chunk(4)))
        {
            // Shuffled the same way every time, so no variant always comes first.
            var order = chunk.OrderBy(e => Stable($"{sceneId}|{kind}|{e.Run.Label}")).ToList();
            var letters = order.Select((_, i) => ((char)('A' + i)).ToString()).ToList();

            var prompt = new StringBuilder();
            prompt.AppendLine($"You are judging {order.Count} versions of the same {(kind == "scene" ? "scene" : "reply to the player")} of a slice-of-life dating sim, written by different writers from the same situation.");
            prompt.AppendLine();
            prompt.AppendLine("## The situation the writers were given");
            prompt.AppendLine(context.Packet.Length > 9000 ? context.Packet[..9000] : context.Packet);
            if (kind == "reaction")
            {
                prompt.AppendLine("## The scene so far");
                prompt.AppendLine(context.SceneText);
                prompt.AppendLine();
                prompt.AppendLine("## What the player replied");
                prompt.AppendLine(context.Reply);
            }

            prompt.AppendLine();
            prompt.AppendLine("## Versions");
            for (var i = 0; i < order.Count; i++)
            {
                var output = order[i].Output;
                prompt.AppendLine($"### {letters[i]}");
                prompt.AppendLine(output.Fallback ? "(No version was written: the writer failed and a placeholder was shown.)\n" + output.Text : output.Text);
                prompt.AppendLine(output.Choices.Count == 0
                    ? "Proposed options for the player: none."
                    : "Proposed options for the player:\n" + string.Join("\n", output.Choices.Select(c => "- " + c)));
                prompt.AppendLine();
            }

            prompt.AppendLine("""
                ## How to judge
                Score every version from 1 (poor) to 5 (excellent) on:
                - specificity: concrete, particular, fresh details and events, rather than generic atmosphere and stock phrases.
                - voice: the people sound like distinct individuals true to their temper; with nobody present, the place feels particular.
                - agency: never says what the player does, says, thinks or feels, beyond the player's own reply.
                - language: natural, correct writing in the story's language, with no stray words from other languages and nothing that reads as a clumsy translation.
                - coherence: consistent with the situation and what is known. New details are fine when nothing contradicts them.
                - options: the proposed options are specific, different in kind and worth choosing between; 1 when there are none.
                - overall: from 1 to 10, how much a reader would enjoy this and want to keep playing.
                Add a one-sentence note in English on each version's biggest strength or weakness. Do not reward length for its own sake.
                """);

            var schema = JudgeSchema(letters);
            JsonNode? answer = null;
            for (var attempt = 0; attempt < 3 && answer is null; attempt++)
            {
                try
                {
                    answer = JsonNode.Parse(await llm.CompleteJsonAsync(new LlmRequest(
                        "You are a demanding fiction editor who judges short interactive fiction fairly and consistently. Answer with JSON matching the schema.",
                        prompt.ToString(), "grades", schema, 4000)));
                }
                catch (Exception ex) when (ex is JsonException or HttpRequestException or InvalidOperationException or TaskCanceledException)
                {
                    Console.WriteLine($"judge failed on scene {sceneId} {kind}: {ex.Message}");
                }
            }

            foreach (var version in answer?["versions"]?.AsArray() ?? [])
            {
                var index = letters.IndexOf(version!["label"]!.GetValue<string>());
                if (index < 0)
                {
                    continue;
                }

                int Score(string name) => version[name]?.GetValue<int>() ?? 0;
                grades.Add(new EvalGrade(
                    sceneId, kind, order[index].Run.Label, Score("specificity"), Score("voice"), Score("agency"), Score("language"),
                    Score("coherence"), Score("options"), Score("overall"), version["note"]?.GetValue<string>() ?? ""));
            }

            Console.WriteLine($"graded scene {sceneId} {kind}: {string.Join(", ", order.Select(e => e.Run.Label))}");
        }
    }

    await File.WriteAllTextAsync(Path.Combine(outDir, "grades.json"), JsonSerializer.Serialize(grades, json));
    await File.WriteAllTextAsync(Path.Combine(outDir, "report.md"), Report(runs, grades, judgeModel));
    Console.WriteLine($"report written to {Path.Combine(outDir, "report.md")}");
    return 0;
}

string Report(IReadOnlyList<EvalRun> runs, IReadOnlyList<EvalGrade> grades, string judgeModel)
{
    var text = new StringBuilder();
    text.AppendLine("# Writing evaluation");
    text.AppendLine();
    text.AppendLine($"Judge: {judgeModel}. Scores 1-5, overall 1-10, averaged over the outputs each run produced.");

    foreach (var kind in new[] { "scene", "reaction" })
    {
        text.AppendLine();
        text.AppendLine($"## {(kind == "scene" ? "Scenes" : "Replies")}");
        text.AppendLine();
        text.AppendLine("| Run | Outputs | Fallback | Attempts | Seconds | Specific | Voice | Agency | Language | Coherence | Options | Overall |");
        text.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var run in runs)
        {
            var outputs = run.Outputs.Where(o => o.Kind == kind).ToList();
            var mine = grades.Where(g => g.Kind == kind && g.Run == run.Label).ToList();
            if (outputs.Count == 0)
            {
                continue;
            }

            string Avg(Func<EvalGrade, int> pick) => mine.Count == 0 ? "-" : mine.Average(pick).ToString("0.00", CultureInfo.InvariantCulture);
            text.AppendLine(
                $"| {run.Label} | {outputs.Count} | {outputs.Count(o => o.Fallback)} | {outputs.Average(o => o.Attempts):0.00} | {outputs.Average(o => o.Seconds):0.0} | " +
                $"{Avg(g => g.Specificity)} | {Avg(g => g.Voice)} | {Avg(g => g.Agency)} | {Avg(g => g.Language)} | {Avg(g => g.Coherence)} | {Avg(g => g.Options)} | {Avg(g => g.Overall)} |");
        }
    }

    text.AppendLine();
    text.AppendLine("## Every version");
    foreach (var group in runs.SelectMany(r => r.Outputs.Select(o => (Run: r, Output: o))).GroupBy(e => (e.Output.SceneId, e.Output.Kind)))
    {
        var first = group.First().Output;
        text.AppendLine();
        text.AppendLine($"### Scene {first.SceneId}, {first.Kind} ({first.Encounter})");
        if (first.Reply is { } reply)
        {
            text.AppendLine();
            text.AppendLine($"Player: {reply}");
        }

        foreach (var (run, output) in group)
        {
            var grade = grades.FirstOrDefault(g => g.SceneId == output.SceneId && g.Kind == output.Kind && g.Run == run.Label);
            text.AppendLine();
            text.AppendLine($"**{run.Label}**{(grade is null ? "" : $" — {grade.Overall}/10: {grade.Note}")}{(output.Fallback ? " (fallback)" : "")}");
            text.AppendLine();
            text.AppendLine(string.Join("\n", output.Text.Split('\n').Select(line => "> " + line)));
            if (output.Choices.Count > 0)
            {
                text.AppendLine();
                text.AppendLine(string.Join("\n", output.Choices.Select(c => "- " + c)));
            }
        }
    }

    return text.ToString();
}

ServiceProvider Services(IDictionary<string, string?> overrides)
{
    var configuration = new ConfigurationBuilder()
        .AddJsonFile(Path.Combine(root, "src", "Game.App", "appsettings.json"))
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Path"] = workDb,
            ["ImageStore:Directory"] = Path.Combine(outDir, "images"),
            ["Llm:RequestTimeout"] = "00:10:00",
        })
        .AddInMemoryCollection(overrides)
        .Build();

    var services = new ServiceCollection();
    services.AddLogging();
    services.AddGame(configuration, root);

    var provider = services.BuildServiceProvider();
    provider.GetRequiredService<Database>().Migrate();
    return provider;
}

static Dictionary<string, string?> Variant(string name)
{
    static Dictionary<string, string?> Toggles(bool twoPass, bool voices, bool happenings, bool threads, bool varied) => new()
    {
        ["Llm:TwoPass"] = twoPass.ToString(),
        ["Llm:Voices"] = voices.ToString(),
        ["Llm:Happenings"] = happenings.ToString(),
        ["Llm:Threads"] = threads.ToString(),
        ["Llm:VariedChoices"] = varied.ToString(),
    };

    return name switch
    {
        "baseline" => Toggles(false, false, false, false, false),
        "twopass" => Toggles(true, false, false, false, false),
        "material" => Toggles(false, true, true, true, true),
        "full" => Toggles(true, true, true, true, true),
        "voices" => Toggles(false, true, false, false, false),
        "happenings" => Toggles(false, false, true, false, false),
        "threads" => Toggles(false, false, false, true, false),
        "varied" => Toggles(false, false, false, false, true),
        _ => throw new InvalidOperationException($"Unknown variant '{name}'."),
    };
}

static int VariantOrder(string variant) => variant switch
{
    "baseline" => 0,
    "twopass" => 1,
    "material" => 2,
    "full" => 3,
    _ => 4,
};

static string Kind(string encounter) =>
    encounter.StartsWith("opening.", StringComparison.Ordinal) ? "opening"
    : encounter.StartsWith("route.", StringComparison.Ordinal) ? "route"
    : encounter.StartsWith("arc.", StringComparison.Ordinal) ? "arc"
    : encounter.StartsWith("beat.first-date", StringComparison.Ordinal) ? "date"
    : encounter;

static JsonObject JudgeSchema(IReadOnlyList<string> letters)
{
    JsonObject Score(int max) => new() { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = max };

    return new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["versions"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["label"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray([.. letters.Select(l => (JsonNode)JsonValue.Create(l)!)]) },
                        ["specificity"] = Score(5),
                        ["voice"] = Score(5),
                        ["agency"] = Score(5),
                        ["language"] = Score(5),
                        ["coherence"] = Score(5),
                        ["options"] = Score(5),
                        ["overall"] = Score(10),
                        ["note"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray("label", "specificity", "voice", "agency", "language", "coherence", "options", "overall", "note"),
                    ["additionalProperties"] = false,
                },
            },
        },
        ["required"] = new JsonArray("versions"),
        ["additionalProperties"] = false,
    };
}

static ulong Stable(string text)
{
    var hash = 14695981039346656037UL;
    foreach (var ch in text)
    {
        hash = unchecked((hash ^ ch) * 1099511628211UL);
    }

    return hash;
}

string? Arg(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static string RepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !dir.EnumerateFiles("Adate.slnx").Any())
    {
        dir = dir.Parent;
    }

    return dir?.FullName ?? throw new InvalidOperationException("Run from inside the repository.");
}

static int Help()
{
    Console.WriteLine("""
        adate writing evaluation

          cases [--db <adate.db>] [--count 10]      copy a database and pick past scenes to replay
          run --variant <name> [--model <id>]       write every case again; variants: baseline, twopass, material, full,
                                                    voices, happenings, threads, varied
          grade --judge <model id> [--runs a,b]     score the versions of each case side by side and write report.md
        """);
    return 0;
}

/// <summary>A past scene to write again, with the loose ends open before it.</summary>
internal sealed record EvalCase(string SaveId, long SceneId, string Encounter, bool HasReply, IReadOnlyList<StoryThread> LooseEnds);

/// <summary>One version: a scene, or the answer to the scene's first reply.</summary>
internal sealed record EvalOutput(
    long SceneId,
    string Kind,
    string Encounter,
    string Packet,
    string? SceneText,
    string? Reply,
    string Text,
    IReadOnlyList<string> Choices,
    bool Fallback,
    int Attempts,
    IReadOnlyList<string> Rejections,
    double Seconds,
    IReadOnlyList<string>? Threads);

internal sealed record EvalRun(string Label, string Variant, string Model, IReadOnlyList<EvalOutput> Outputs);

internal sealed record EvalGrade(
    long SceneId, string Kind, string Run, int Specificity, int Voice, int Agency, int Language, int Coherence, int Options, int Overall, string Note);
