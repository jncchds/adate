using Game.Core.Content;
using Game.Core.Encounters;
using Game.Core.Scenes;
using Game.Core.Settings;
using Game.Core.Story;
using Game.Core.World;

namespace Game.Core.Tests;

public class RoutineKnowledgeTests
{
    private static string ContentPath(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.EnumerateFiles("*.slnx").Any() is false)
        {
            dir = dir.Parent;
        }

        return Path.Combine([dir?.FullName ?? throw new InvalidOperationException("No repository root."), "content", .. parts]);
    }

    private static readonly SettingDefinition Camp =
        new JsonSettingCatalog(ContentPath("settings"), new JsonLocationCatalog(ContentPath("place-types.json"))).Get("summer-camp");

    private static readonly CharacterSchedule Week = new("asya",
    [
        new ScheduleEntry(TimeOfDay.Morning, "lake-dock", ScheduleGenerator.Weekdays),
        new ScheduleEntry(TimeOfDay.Evening, "campfire-circle", ScheduleGenerator.Weekend),
        new ScheduleEntry(TimeOfDay.Night, "story-asyas-cabin"),
    ]);

    [Fact]
    public void Only_the_parts_of_a_week_the_player_was_given_or_learned_are_known()
    {
        var flags = new Dictionary<string, string>
        {
            ["chance.routine.weekend.evening"] = "campfire-circle",
            ["chance.routine.weekdays.midday"] = "mess-hall",
        };

        var known = RoutineKnowledge.Known(Week, "chance", flags, [new RoutineEntry(RoutineKnowledge.Daily, TimeOfDay.Night, "story-asyas-cabin")]);

        Assert.Equal([TimeOfDay.Evening, TimeOfDay.Night], known.Select(e => e.Slot));
        Assert.Equal(
            "weekend evenings at the campfire; nights at Asya's cabin",
            RoutineKnowledge.Describe(known, id => id == "campfire-circle" ? "the campfire" : "Asya's cabin"));
    }

    [Fact]
    public void Finding_someone_where_their_week_puts_them_twice_teaches_that_part()
    {
        var weekdayMorning = new ClockState(2, TimeOfDay.Morning);
        var entry = RoutineKnowledge.Match(Week, weekdayMorning, "lake-dock");

        Assert.NotNull(entry);
        Assert.Null(RoutineKnowledge.Match(Week, weekdayMorning, "mess-hall"));
        Assert.Null(RoutineKnowledge.Match(Week, new ClockState(6, TimeOfDay.Morning), "lake-dock"));

        var flags = new Dictionary<string, string>();
        var toSet = new Dictionary<string, string>();
        RoutineKnowledge.See(toSet, flags, "chance", entry!);
        Assert.False(toSet.ContainsKey(RoutineKnowledge.LearnedKey("chance", entry!)));

        RoutineKnowledge.See(toSet, flags, "chance", entry!);
        Assert.Equal("lake-dock", toSet[RoutineKnowledge.LearnedKey("chance", entry!)]);
    }

    [Fact]
    public void A_known_home_adds_nights_there_and_changes_nothing_else_about_the_week()
    {
        var without = ScheduleGenerator.For("asya", Camp, "lake-dock", TimeOfDay.Morning, 42);
        var with = ScheduleGenerator.For("asya", Camp, "lake-dock", TimeOfDay.Morning, 42, "story-asyas-cabin");

        Assert.Equal(without.Entries, with.Entries.Take(without.Entries.Count));
        Assert.Equal("story-asyas-cabin", with.Where(new ClockState(3, TimeOfDay.Night)));
        Assert.Null(without.Where(new ClockState(3, TimeOfDay.Night)));
    }

    [Fact]
    public void A_meeting_can_remember_the_time_of_day_it_happened()
    {
        var encounter = new EncounterDefinition(
            "test.meet", new EncounterPlace(Id: "lake-dock"), Sets: ["chance.place={place}", "chance.slot={slot}"], Text: "Hello.");

        var outcome = TurnPlanner.Plan(
            Camp, [encounter], new TurnContext(new ClockState(3, TimeOfDay.Afternoon), "lake-dock", new Dictionary<string, string>(), 0), "The waterfront");

        Assert.Equal("lake-dock", outcome.FlagsToSet["chance.place"]);
        Assert.Equal("Afternoon", outcome.FlagsToSet["chance.slot"]);
    }
}
