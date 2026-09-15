using System.Diagnostics;
using System.Globalization;
using Game.Core;
using Game.Core.Cast;
using Game.Core.Characters;
using Game.Core.Saves;
using Game.Core.Settings;
using Game.Core.Story;
using Game.Data.Repositories;
using Game.Llm;
using Game.Play;
using Microsoft.Extensions.Options;

namespace Game.Host.Services;

/// <summary>
/// The phase-2 plan's LLM measures (build step 9), run against the configured model with the game's
/// own services and no images:
/// <list type="bullet">
/// <item><c>measure run</c>: a scripted playthrough from new game to ending, reporting the fallback rate.</item>
/// <item><c>measure judge</c>: the judge's accuracy on planted contradictions.</item>
/// <item><c>measure retrieval</c>: whether embeddings retrieve the right memory better than recency alone.</item>
/// </list>
/// </summary>
public static class MeasureRunner
{
    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        var llm = services.GetRequiredService<IOptions<LlmOptions>>().Value;
        Console.WriteLine($"LLM: {(llm.Enabled ? llm.Model : "disabled")} via {llm.Provider} at {llm.ChatAddress}; embeddings: {(string.IsNullOrEmpty(llm.EmbeddingModel) ? "none" : llm.EmbeddingModel)}");

        return args.FirstOrDefault() switch
        {
            "run" => await PlaythroughAsync(services, args),
            "judge" => await JudgeAsync(services),
            "retrieval" => await RetrievalAsync(services),
            "pictures" => await PicturesAsync(services, args),
            _ => Usage(),
        };
    }

    /// <summary>
    /// Renders what the picture cards show for a save, without playing to a slot that offers them:
    /// the shared backdrop and every cast member's portrait sprite. Prints each image's path.
    /// </summary>
    private static async Task<int> PicturesAsync(IServiceProvider services, string[] args)
    {
        var saves = services.GetRequiredService<SaveRepository>();
        var studio = services.GetRequiredService<CharacterStudio>();
        var world = services.GetRequiredService<WorldService>();
        var characters = services.GetRequiredService<CharacterRepository>();
        var castContent = services.GetRequiredService<CastContent>();

        var saveArg = Arg(args, "--save") ?? throw new InvalidOperationException("pictures needs --save <id>.");
        var saveId = (await saves.ListAsync()).FirstOrDefault(s => s.Id.Value.ToString() == saveArg)?.Id
            ?? throw new InvalidOperationException($"No save '{saveArg}'.");

        Console.WriteLine($"backdrop {await studio.GenerateBackdropAsync()}");

        // Keys as invites use them: main_li, or a variant's route id without its reference prefix.
        var play = await world.GetPlayStateAsync(saveId);
        foreach (var (reference, name) in play.People)
        {
            var key = reference.StartsWith(Game.Core.Encounters.JsonEncounterCatalog.VariantPrefix, StringComparison.Ordinal)
                ? reference[Game.Core.Encounters.JsonEncounterCatalog.VariantPrefix.Length..]
                : reference;
            Console.WriteLine($"{key} ({name}) {await world.PortraitAsync(saveId, key) ?? "(not in the cast)"}");
        }

        return 0;
    }

    private static int Usage()
    {
        Console.WriteLine("""
            usage: dotnet run --project src/Game.Host -- measure <run|judge|retrieval>
              run [--setting big-city] [--opening <id>]   scripted playthrough, fallback rate and latency
                [--language <name>]                      write the new save's story in that language
              judge                                      judge accuracy on planted contradictions
              retrieval                                  hit@3 with embeddings against recency alone
              pictures --save <id>                       render the backdrop and cast portraits the cards use
            """);
        return 2;
    }

    // -------------------------------------------------------------------- playthrough

    private static async Task<int> PlaythroughAsync(IServiceProvider services, string[] args)
    {
        var saves = services.GetRequiredService<SaveRepository>();
        var characters = services.GetRequiredService<CharacterRepository>();
        var studio = services.GetRequiredService<CharacterStudio>();
        var world = services.GetRequiredService<WorldService>();
        var story = services.GetRequiredService<StoryStateRepository>();
        var settings = services.GetRequiredService<ISettingCatalog>();
        var cast = services.GetRequiredService<CastContent>();

        var setting = settings.Get(Arg(args, "--setting") ?? "big-city");
        var opening = setting.Openings.FirstOrDefault(o => o.Id == Arg(args, "--opening")) ?? setting.Openings[0];
        var varied = Arg(args, "--policy") == "varied";

        // A new game the way the form makes one, with the first option of every feature.
        // --save <id> carries on an existing save instead of starting one; --stop-at-ending leaves the
        // ending offer on screen for a person to pick, rather than taking the first route.
        var stopAtEnding = args.Contains("--stop-at-ending");
        SaveId saveId;

        if (Arg(args, "--save") is { } resume)
        {
            saveId = (await saves.ListAsync()).FirstOrDefault(s => s.Id.Value.ToString() == resume)?.Id
                ?? throw new InvalidOperationException($"No save '{resume}'.");
            var resumed = await world.GetPlayStateAsync(saveId);
            setting = resumed.Setting;
            opening = resumed.Opening ?? opening;
            Console.WriteLine($"resuming save {resume} in {setting.Id} on day {resumed.Clock.Day}, {resumed.Clock.Slot}");
        }
        else
        {
            var pack = await studio.GetPackAsync();
            var subject = pack.SubjectFor("female");
            string First(string feature) => subject.OptionsFor(feature).First(o => !o.PlayerOnly).Tag;

            var appearance = new CharacterAppearance(
                "female", 24,
                First(AppearanceFeatures.EyeColor), First(AppearanceFeatures.HairColor), First(AppearanceFeatures.HairStyle),
                First(AppearanceFeatures.SkinTone), First(AppearanceFeatures.Build), First(AppearanceFeatures.Height), "");
            appearance.Validate();

            var created = await saves.CreateAsync(studio.StylePackId, studio.PackFingerprint(), Ceiling.PG13, setting.Id, "Alex", "woman", Game.Core.Story.NarrationLanguage.Normalize(Arg(args, "--language")));
            var main = await characters.CreateAsync(created.Id, appearance, "Rin", cast.Temper.ToDictionary(a => a.Id, a => a.Ends[0].Id));
            await studio.EnsureCastAsync((await characters.GetAsync(main.Id))!);
            await world.ChooseOpeningAsync(created.Id, opening.Id);
            saveId = created.Id;

            Console.WriteLine($"save {saveId.Value} in {setting.Id}, opening {opening.Id}");
        }

        var save = new { Id = saveId };

        var latencies = new List<double>();
        var turns = 0;
        var choices = 0;
        var visits = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var guard = 0; guard < 400; guard++)
        {
            var play = await world.GetPlayStateAsync(save.Id);

            if (play.Ending is not null)
            {
                break;
            }

            if (play.EndingOffer is not null && stopAtEnding)
            {
                Console.WriteLine($"day {play.Clock.Day}: stopped at the ending offer");
                break;
            }

            if (play.EndingOffer is { } offer)
            {
                var pick = offer.Routes.FirstOrDefault()?.Key ?? EndingRules.AloneKey;
                await world.EndAsync(save.Id, pick);
                Console.WriteLine($"day {play.Clock.Day}: ended with '{pick}'");
                continue;
            }

            if (play.PendingScene is { } waiting)
            {
                // The first proposed reply, or with --policy varied each one in turn.
                await world.RespondAsync(save.Id, varied ? choices % waiting.Choices.Count : 0, null);
                choices++;
                continue;
            }

            if (play.Pending is { } pending)
            {
                // The first answer is the warmer one in every authored choice; varied takes each in turn.
                await world.ChooseAsync(save.Id, pending.Choices[varied ? choices % pending.Choices.Count : 0].Id);
                choices++;
                continue;
            }

            // Follow the story's hints, bring someone along whenever an encounter would honour it,
            // and otherwise spread visits over the places the player knows.
            var place = PlaceFor(play, opening, visits);
            var invite = play.Invitees.FirstOrDefault()?.Key;

            Game.Core.Encounters.TurnOutcome outcome;
            try
            {
                outcome = await world.TakeTurnAsync(save.Id, place, invite);
            }
            catch (InvalidOperationException) when (invite is not null)
            {
                outcome = await world.TakeTurnAsync(save.Id, place);
            }

            visits[place] = visits.GetValueOrDefault(place) + 1;
            turns++;

            // Every turn is written now; quiet turns carry their own encounter ids.
            {
                var clock = Stopwatch.StartNew();
                await world.WriteSceneAsync(save.Id, outcome);
                latencies.Add(clock.Elapsed.TotalSeconds);
                Console.WriteLine($"day {outcome.VisitedAt.Day} {outcome.VisitedAt.Slot,-9} {outcome.EncounterId,-40} {clock.Elapsed.TotalSeconds,5:0.0}s");
            }
        }

        await ReportPlaythroughAsync(services, save.Id, turns, choices, latencies);
        return 0;
    }

    private static string PlaceFor(PlayState play, SettingOpening opening, Dictionary<string, int> visits)
    {
        var known = play.KnownPlaces.Select(p => p.Id).ToList();

        // An event today is where the story is.
        if (play.Today.FirstOrDefault(e => e.Time == play.Clock.Slot && known.Contains(e.Place)) is { } ev)
        {
            return ev.Place;
        }

        // The opening's first week points at its places in its slot.
        if (play.Clock.Day <= 4 && play.Clock.Slot == opening.Time)
        {
            return play.Clock.Day <= 2 ? opening.MeetingPlace : opening.HomePlace;
        }

        return known.OrderBy(id => visits.GetValueOrDefault(id)).ThenBy(id => id, StringComparer.Ordinal).First();
    }

    private static async Task ReportPlaythroughAsync(IServiceProvider services, SaveId saveId, int turns, int choices, IReadOnlyList<double> latencies)
    {
        var database = services.GetRequiredService<Game.Data.Database>();
        await using var connection = await database.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT kind, json_extract(payload_json, '$.attempts'), json_extract(payload_json, '$.rejections')
            FROM turn_log WHERE save_id = $save ORDER BY id;
            """;
        command.Parameters.AddWithValue("$save", saveId.ToString());

        var written = 0;
        var fallbacks = 0;
        var attempts = new List<int>();
        var reasons = new Dictionary<string, int>(StringComparer.Ordinal);

        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                if (reader.GetString(0) == "scene-fallback")
                {
                    fallbacks++;
                }
                else
                {
                    written++;
                }

                attempts.Add(reader.IsDBNull(1) ? 0 : reader.GetInt32(1));

                if (!reader.IsDBNull(2))
                {
                    foreach (var reason in System.Text.Json.JsonSerializer.Deserialize<List<string>>(reader.GetString(2)) ?? [])
                    {
                        var key = Category(reason);
                        reasons[key] = reasons.GetValueOrDefault(key) + 1;
                    }
                }
            }
        }

        var story = services.GetRequiredService<StoryStateRepository>();
        var ending = await story.GetEndingAsync(saveId);
        var scenes = written + fallbacks;
        var sorted = latencies.Order().ToList();

        Console.WriteLine();
        Console.WriteLine("== playthrough");
        Console.WriteLine($"turns {turns}, choices {choices}, scenes {scenes}");
        Console.WriteLine($"written {written}, fallback {fallbacks}, fallback rate {(scenes == 0 ? 0 : 100.0 * fallbacks / scenes):0.0}% (plan target: under 5%)");
        Console.WriteLine($"attempts per scene: first try {attempts.Count(a => a == 1)}, second {attempts.Count(a => a == 2)}, third {attempts.Count(a => a == 3)}");
        Console.WriteLine($"latency per scene: mean {(sorted.Count == 0 ? 0 : sorted.Average()):0.0}s, median {Percentile(sorted, 0.5):0.0}s, p90 {Percentile(sorted, 0.9):0.0}s, max {(sorted.Count == 0 ? 0 : sorted[^1]):0.0}s");
        Console.WriteLine("rejections by kind: " + (reasons.Count == 0 ? "none" : string.Join(", ", reasons.OrderByDescending(r => r.Value).Select(r => $"{r.Key} {r.Value}"))));
        Console.WriteLine($"ending: {(ending is null ? "none" : ending.Kind.ToString())} on day {ending?.Day}");
    }

    private static string Category(string reason) =>
        reason.Contains("could not answer", StringComparison.Ordinal) ? "unreachable"
        : reason.Contains("first person", StringComparison.Ordinal) ? "first person"
        : reason.Contains("decides for the player", StringComparison.Ordinal) ? "player action"
        : reason.Contains("one block", StringComparison.Ordinal) ? "no paragraphs"
        : reason.Contains("characters; keep it under", StringComparison.Ordinal) ? "too long"
        : reason.Contains("expression", StringComparison.Ordinal) ? "expression"
        : reason.Contains("contradicts an established fact", StringComparison.Ordinal) ? "judge"
        : reason.Contains("cannot change", StringComparison.Ordinal) || reason.Contains("needs an event", StringComparison.Ordinal) ? "fact contradiction"
        : reason.Contains("do not know", StringComparison.Ordinal) ? "knowledge"
        : reason.Contains("place", StringComparison.OrdinalIgnoreCase) ? "place"
        : reason.Contains("not valid JSON", StringComparison.Ordinal) ? "json"
        : "other";

    private static double Percentile(IReadOnlyList<double> sorted, double p) =>
        sorted.Count == 0 ? 0 : sorted[(int)Math.Clamp(Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];

    private static string? Arg(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    // -------------------------------------------------------------------- judge

    private static readonly KnownFact[] JudgeFacts =
    [
        Known(1, "hair-color", "black hair"),
        Known(2, "eye-color", "brown eyes"),
        Known(3, "age", "24"),
        Known(4, "hair-style", "short hair"),
        Known(5, "met-at", "The Corner Cup"),
    ];

    private static KnownFact Known(long id, string predicate, string value) =>
        new(id, new Fact("rin", predicate, value, FactLevel.Core, "appearance", 0), new HashSet<string> { FactLedger.Player, "rin" });

    /// <summary>Scenes about Rin: half contradict one fixed fact, half are consistent with all of them.</summary>
    private static readonly (string Text, bool Contradicts)[] JudgeCases =
    [
        ("Rin pushes a strand of long blonde hair out of their face and grins at you.", true),
        ("Rin's green eyes catch the light from the window as they laugh.", true),
        ("Rin mentions they turned forty last spring and still feel twenty.", true),
        ("Rin's waist-length hair falls over the back of the chair.", true),
        ("Rin reminds you that the two of you first met at the train station.", true),
        ("You notice Rin has dyed their hair bright red since you last saw them.", true),
        ("Rin blinks their pale blue eyes slowly, still half asleep.", true),
        ("Rin, barely nineteen, fumbles with the coffee machine.", true),
        ("Rin ties their long braid back before starting work.", true),
        ("Rin smiles and says it's been ages since that day you met at the library.", true),
        ("Rin runs a hand through their short black hair and sighs.", false),
        ("Rin's brown eyes crinkle when they smile at your joke.", false),
        ("Rin says twenty-four has been a strange year so far.", false),
        ("Rin taps the table at The Corner Cup, where the two of you first met.", false),
        ("Rin shrugs off their coat and orders a tea for both of you.", false),
        ("Rain streaks the window while Rin tells you about their week.", false),
        ("Rin's short hair is damp from the rain outside.", false),
        ("Rin looks at you with steady brown eyes and waits for an answer.", false),
        ("Rin laughs, the sound bright against the hum of the cafe.", false),
        ("Rin checks the time and says they can stay another hour.", false),
    ];

    private static async Task<int> JudgeAsync(IServiceProvider services)
    {
        var judge = services.GetRequiredService<SceneJudge>();
        var names = new Dictionary<string, string> { ["rin"] = "Rin", [FactLedger.Player] = "Alex" };

        int tp = 0, fp = 0, fn = 0, tn = 0;
        var clock = Stopwatch.StartNew();

        foreach (var (text, contradicts) in JudgeCases)
        {
            var found = await judge.CheckAsync(text, JudgeFacts, names);
            var flagged = found.Count > 0;

            (tp, fp, fn, tn) = (contradicts, flagged) switch
            {
                (true, true) => (tp + 1, fp, fn, tn),
                (false, true) => (tp, fp + 1, fn, tn),
                (true, false) => (tp, fp, fn + 1, tn),
                _ => (tp, fp, fn, tn + 1),
            };

            Console.WriteLine($"{(contradicts == flagged ? "ok  " : "MISS")} {(contradicts ? "planted" : "clean  ")} {text}{(flagged ? "  -> " + string.Join(" / ", found) : "")}");
        }

        Console.WriteLine();
        Console.WriteLine("== judge");
        Console.WriteLine($"planted contradictions caught {tp}/{tp + fn} (recall {Ratio(tp, tp + fn)}), clean scenes flagged {fp}/{fp + tn}, precision {Ratio(tp, tp + fp)}");
        Console.WriteLine($"{JudgeCases.Length} checks in {clock.Elapsed.TotalSeconds:0.0}s");
        return 0;
    }

    private static string Ratio(int a, int b) => b == 0 ? "n/a" : (100.0 * a / b).ToString("0", CultureInfo.InvariantCulture) + "%";

    // -------------------------------------------------------------------- retrieval

    /// <summary>A memory per day, and a question that should bring that memory back.</summary>
    private static readonly (string Memory, string Query)[] RetrievalCases =
    [
        ("Rin showed you the bookshop's hidden poetry shelf.", "Rin is holding a thin volume of verse from Paper Lantern Books."),
        ("You and Rin got caught in a downpour and hid under the bridge.", "Storm clouds are gathering over the river again."),
        ("Rin admitted they are scared of dogs since childhood.", "A golden retriever bounds up to the table."),
        ("Rin burned the pancakes at the staff breakfast and laughed about it.", "The smell of something scorched drifts from the kitchen."),
        ("You helped Rin carry boxes up four flights of stairs on moving day.", "Rin groans at the lift being out of order."),
        ("Rin sang badly at karaoke and dared you to join in.", "A karaoke flyer is pinned by the door."),
        ("Rin told you about the shelter's rent going up.", "Rin is counting coins from a donation jar."),
        ("You watched the harbour fireworks from the rooftop garden with Rin.", "Someone mentions another fireworks night next month."),
        ("Rin lost a chess game to you and demanded a rematch.", "A chessboard sits set up on the corner table."),
        ("Rin cried a little at the end of an old film.", "The cinema is showing a black-and-white classic tonight."),
        ("You and Rin argued about whether pineapple belongs on pizza.", "Rin orders a Hawaiian slice and looks at you pointedly."),
        ("Rin's sister called and Rin went quiet for the rest of the evening.", "Rin's phone lights up with their sister's name."),
    ];

    private static async Task<int> RetrievalAsync(IServiceProvider services)
    {
        var embeddings = services.GetRequiredService<IEmbeddingClient>();
        string[] present = ["rin"];
        var memories = new List<MemoryEntry>();

        for (var i = 0; i < RetrievalCases.Length; i++)
        {
            memories.Add(new MemoryEntry(i + 1, MemoryScope.Scene, i + 1, RetrievalCases[i].Memory, present, [], await embeddings.EmbedAsync(RetrievalCases[i].Memory)));
        }

        var today = RetrievalCases.Length + 1;
        int embeddedHits = 0, recencyHits = 0;

        for (var i = 0; i < RetrievalCases.Length; i++)
        {
            var query = await embeddings.EmbedAsync(RetrievalCases[i].Query);
            var withEmbeddings = MemoryRetrieval.Retrieve(memories, query, present, today, top: 3);
            var byRecency = MemoryRetrieval.Retrieve(memories, null, present, today, top: 3);

            var hit = withEmbeddings.Any(m => m.Id == i + 1);
            embeddedHits += hit ? 1 : 0;
            recencyHits += byRecency.Any(m => m.Id == i + 1) ? 1 : 0;

            Console.WriteLine($"{(hit ? "hit " : "miss")} {RetrievalCases[i].Query}  -> {withEmbeddings[0].Summary}");
        }

        Console.WriteLine();
        Console.WriteLine("== retrieval");
        Console.WriteLine($"hit@3 with embeddings {embeddedHits}/{RetrievalCases.Length} ({Ratio(embeddedHits, RetrievalCases.Length)}), by recency alone {recencyHits}/{RetrievalCases.Length} ({Ratio(recencyHits, RetrievalCases.Length)})");
        return 0;
    }
}
