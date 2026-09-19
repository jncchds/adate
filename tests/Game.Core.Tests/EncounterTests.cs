using Game.Core.Encounters;
using Game.Core.Scenes;
using Game.Core.Settings;
using Game.Core.World;

namespace Game.Core.Tests;

public class EncounterTests
{
    private static SettingDefinition Setting() => new(
        "test-town",
        "Test town",
        Days: 10,
        Tone: "Quiet.",
        RoutinePlace: "cafe-1",
        Places:
        [
            new SettingPlace("cafe-1", "cafe", "Cafe"),
            new SettingPlace("bar-1", "cafe", "Bar", Known: false),
            new SettingPlace("park-1", "cafe", "Park"),
        ],
        Openings: [],
        Events: [new SettingEvent("fair", "The fair", 5, "park-1", TimeOfDay.Evening)],
        Occupations: ["clerk"]);

    private static TurnContext At(
        int day = 1,
        TimeOfDay slot = TimeOfDay.Morning,
        string place = "cafe-1",
        IReadOnlyDictionary<string, string>? flags = null,
        int alone = 0) =>
        new(new ClockState(day, slot), place, flags ?? new Dictionary<string, string>(), alone);

    private static EncounterDefinition E(string id, string? place = "cafe-1", int priority = 50) =>
        new(id, new EncounterPlace(Id: place), Priority: priority, Text: $"{id} happens.");

    // ------------------------------------------------------------------ clock

    [Fact]
    public void The_clock_moves_through_the_slots_and_rolls_over_to_the_next_morning()
    {
        var clock = ClockState.Start;
        Assert.Equal(new ClockState(1, TimeOfDay.Morning), clock);

        for (var i = 0; i < 4; i++)
        {
            clock = clock.Next();
        }

        Assert.Equal(new ClockState(1, TimeOfDay.Night), clock);
        Assert.Equal(new ClockState(2, TimeOfDay.Morning), clock.Next());
    }

    [Fact]
    public void A_calendar_runs_out_after_its_last_day()
    {
        Assert.False(new ClockState(10, TimeOfDay.Night).IsPast(10));
        Assert.True(new ClockState(10, TimeOfDay.Night).Next().IsPast(10));
    }

    // ------------------------------------------------------------- matching

    [Fact]
    public void An_encounter_matches_its_place_time_and_days()
    {
        var encounter = E("x") with { Time = [TimeOfDay.Evening], Days = [3, 6] };

        Assert.True(EncounterEvaluator.Matches(encounter, At(day: 4, slot: TimeOfDay.Evening)));
        Assert.False(EncounterEvaluator.Matches(encounter, At(day: 4, slot: TimeOfDay.Morning)));
        Assert.False(EncounterEvaluator.Matches(encounter, At(day: 7, slot: TimeOfDay.Evening)));
        Assert.False(EncounterEvaluator.Matches(encounter, At(day: 4, slot: TimeOfDay.Evening, place: "park-1")));
    }

    [Theory]
    [InlineData("met", true)]
    [InlineData("!met", false)]
    [InlineData("route=chance", true)]
    [InlineData("route=main", false)]
    [InlineData("contact", false)]
    [InlineData("!contact", true)]
    public void Flag_expressions_hold_as_written(string expression, bool expected)
    {
        var flags = new Dictionary<string, string> { ["met"] = "true", ["route"] = "chance", ["contact"] = "false" };

        Assert.Equal(expected, EncounterEvaluator.Holds(flags, expression));
    }

    [Fact]
    public void A_once_only_encounter_does_not_fire_again()
    {
        var fired = new Dictionary<string, string> { [EncounterEvaluator.FiredKey("x")] = "2" };

        Assert.False(EncounterEvaluator.Matches(E("x"), At(flags: fired)));
        Assert.True(EncounterEvaluator.Matches(E("x") with { Once = false }, At(flags: fired)));
    }

    /// <summary>The main LI's home place is decided by the opening, so encounters there read it from a flag.</summary>
    [Fact]
    public void A_place_flag_matches_only_the_place_it_names()
    {
        var encounter = new EncounterDefinition("home", new EncounterPlace(PlaceFlag: "main_li.home_place"), Text: "t");
        var flags = new Dictionary<string, string> { ["main_li.home_place"] = "park-1" };

        Assert.True(EncounterEvaluator.Matches(encounter, At(place: "park-1", flags: flags)));
        Assert.False(EncounterEvaluator.Matches(encounter, At(place: "cafe-1", flags: flags)));
        Assert.False(EncounterEvaluator.Matches(encounter, At(place: "park-1")));
    }

    [Fact]
    public void A_solo_visit_condition_waits_for_enough_earlier_visits()
    {
        var encounter = new EncounterDefinition("regular", new EncounterPlace(Id: "bar-1", AloneVisitsBefore: 2), Text: "t");

        Assert.False(EncounterEvaluator.Matches(encounter, At(place: "bar-1", alone: 1)));
        Assert.True(EncounterEvaluator.Matches(encounter, At(place: "bar-1", alone: 2)));
    }

    [Fact]
    public void The_highest_priority_wins_and_ties_go_to_the_lowest_id()
    {
        EncounterDefinition[] encounters = [E("b"), E("a"), E("low", priority: 10)];
        Assert.Equal("a", EncounterEvaluator.Pick(encounters, At())!.Id);

        EncounterDefinition[] withEvent = [E("a"), E("event", priority: 100)];
        Assert.Equal("event", EncounterEvaluator.Pick(withEvent, At())!.Id);
    }

    // ------------------------------------------------------------- planning

    [Fact]
    public void A_slot_with_no_encounter_is_ambient_and_only_moves_the_clock()
    {
        var outcome = TurnPlanner.Plan(Setting(), [E("elsewhere", place: "park-1")], At(slot: TimeOfDay.Afternoon), "Cafe");

        Assert.Null(outcome.EncounterId);
        Assert.StartsWith("Afternoon at Cafe.", outcome.Text, StringComparison.Ordinal);

        // The narrator never decides for the player, even in the placeholder.
        Assert.DoesNotContain("You ", outcome.Text, StringComparison.Ordinal);
        Assert.Empty(outcome.FlagsToSet);
        Assert.Empty(outcome.Reveals);
        Assert.Equal(new ClockState(1, TimeOfDay.Evening), outcome.Next);
    }

    [Fact]
    public void A_fired_encounter_sets_its_flags_marks_itself_fired_and_reveals_places()
    {
        var encounter = E("tip") with { Sets = ["knows.bar", "route=chance"], Reveals = ["bar-1"], With = ["main_li"] };

        var outcome = TurnPlanner.Plan(Setting(), [encounter], At(day: 3), "Cafe");

        Assert.Equal("tip", outcome.EncounterId);
        Assert.Equal("tip happens.", outcome.Text);
        Assert.Equal("true", outcome.FlagsToSet["knows.bar"]);
        Assert.Equal("chance", outcome.FlagsToSet["route"]);
        Assert.Equal("3", outcome.FlagsToSet[EncounterEvaluator.FiredKey("tip")]);
        Assert.Equal(["bar-1"], outcome.Reveals);
        Assert.Equal(["main_li"], outcome.With);
    }

    [Fact]
    public void The_last_slot_of_the_last_day_ends_the_game_and_no_turn_follows()
    {
        var last = TurnPlanner.Plan(Setting(), [], At(day: 10, slot: TimeOfDay.Night), "Cafe");
        Assert.True(last.GameOver);

        Assert.Throws<InvalidOperationException>(() => TurnPlanner.Plan(Setting(), [], At(day: 11), "Cafe"));
    }

    [Fact]
    public void An_event_place_is_revealed_on_its_day()
    {
        Assert.Equal(["park-1"], TurnPlanner.DayStartReveals(Setting(), 5));
        Assert.Empty(TurnPlanner.DayStartReveals(Setting(), 4));
    }
}

/// <summary>Encounter files are references; each broken one is refused at load with its reference named.</summary>
public sealed class EncounterCatalogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "adate-encounter-tests", Guid.NewGuid().ToString("N"));

    public EncounterCatalogTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory is not worth failing a test run over.
        }
    }

    private sealed class OneSetting : ISettingCatalog
    {
        public static readonly SettingDefinition Town = new(
            "test-town", "Test town", 10, "Quiet.", "cafe-1",
            [new SettingPlace("cafe-1", "cafe", "Cafe"), new SettingPlace("bar-1", "cafe", "Bar", Known: false)],
            [],
            [new SettingEvent("fair", "The fair", 5, "cafe-1", TimeOfDay.Evening)],
            ["clerk"]);

        public SettingDefinition Get(string settingId) => Town;

        public IReadOnlyList<SettingDefinition> All() => [Town];
    }

    private const string Valid = """
        [
          { "id": "tip", "place": { "id": "cafe-1" }, "time": ["Morning"], "days": [2, 6], "requires": ["!knows.bar"], "sets": ["knows.bar"], "reveals": ["bar-1"], "with": ["main_li"], "text": "A tip." }
        ]
        """;

    private JsonEncounterCatalog Load(string json, string fileName = "test-town")
    {
        File.WriteAllText(Path.Combine(_directory, fileName + ".json"), json);
        return new JsonEncounterCatalog(_directory, new OneSetting());
    }

    [Fact]
    public void A_valid_file_loads_with_the_settings_events_added()
    {
        var encounters = Load(Valid).For(OneSetting.Town);

        Assert.Equal(["tip", "event.fair"], encounters.Select(e => e.Id));

        var fair = encounters[1];
        Assert.Equal(JsonEncounterCatalog.EventPriority, fair.Priority);
        Assert.Equal([5, 5], fair.Days);
        Assert.Equal([TimeOfDay.Evening], fair.Time);
    }

    [Theory]
    [InlineData("\"id\": \"cafe-1\"", "\"id\": \"castle\"", "happens at 'castle'")]
    [InlineData("\"reveals\": [\"bar-1\"]", "\"reveals\": [\"attic\"]", "reveals 'attic'")]
    [InlineData("\"days\": [2, 6]", "\"days\": [2, 40]", "days [2, 40]")]
    [InlineData("\"days\": [2, 6]", "\"days\": [6, 2]", "days [6, 2]")]
    [InlineData("\"requires\": [\"!knows.bar\"]", "\"requires\": [\"Knows Bar\"]", "requires 'Knows Bar'")]
    [InlineData("\"sets\": [\"knows.bar\"]", "\"sets\": [\"!knows.bar\"]", "sets '!knows.bar'")]
    [InlineData("\"with\": [\"main_li\"]", "\"with\": [\"the barista\"]", "with 'the barista'")]
    [InlineData("\"text\": \"A tip.\"", "\"text\": \"\"", "has no text")]
    public void A_broken_reference_is_refused_by_name(string from, string to, string expected)
    {
        Assert.Contains(from, Valid, StringComparison.Ordinal);

        var ex = Assert.Throws<InvalidOperationException>(() => Load(Valid.Replace(from, to, StringComparison.Ordinal)));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_duplicate_id_is_refused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Load(Valid.Replace("\"id\": \"tip\"", "\"id\": \"event.fair\"", StringComparison.Ordinal)));

        Assert.Contains("duplicate id", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A typo in a file name would otherwise drop a setting's encounters without a word.</summary>
    [Fact]
    public void A_file_that_matches_no_setting_is_refused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Load(Valid, fileName: "test-twon"));

        Assert.Contains("matches no setting", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Common_encounters_are_shared_and_validated_against_each_setting()
    {
        File.WriteAllText(Path.Combine(_directory, "common.json"), """
            [ { "id": "stroll", "place": {}, "once": false, "text": "A stroll." } ]
            """);

        var encounters = Load(Valid).For(OneSetting.Town);

        Assert.Equal(["stroll", "tip", "event.fair"], encounters.Select(e => e.Id));
        Assert.False(encounters[0].Once);
    }
}
