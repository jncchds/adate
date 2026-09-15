using Game.Core.Scenes;
using Game.Core.Story;
using Game.Core.World;

namespace Game.Core.Tests;

public class WorldMovesTests
{
    private static readonly IReadOnlyList<string> Places = ["cafe", "park", "bar", "library", "pier"];

    private static readonly CharacterSchedule Schedule = new("li-1",
    [
        new ScheduleEntry(TimeOfDay.Morning, "cafe", null),
        new ScheduleEntry(TimeOfDay.Evening, "bar", null),
    ]);

    private static IEnumerable<ClockState> Month() =>
        Enumerable.Range(1, 28).SelectMany(day => new[] { TimeOfDay.Morning, TimeOfDay.Midday, TimeOfDay.Afternoon, TimeOfDay.Evening, TimeOfDay.Night }
            .Select(slot => new ClockState(day, slot)));

    [Fact]
    public void People_are_mostly_where_their_schedule_says_and_sometimes_elsewhere_but_never_out_at_night()
    {
        var mornings = Month().Where(c => c.Slot == TimeOfDay.Morning).Select(c => WorldMoves.Where("save", Schedule, Places, c)).ToList();
        var atCafe = mornings.Count(w => w == "cafe");

        Assert.InRange(atCafe, mornings.Count / 2, mornings.Count - 1);
        Assert.Contains(mornings, w => w is not null && w != "cafe");
        Assert.Contains(Month().Where(c => c.Slot == TimeOfDay.Midday), c => WorldMoves.Where("save", Schedule, Places, c) is not null);
        Assert.All(Month().Where(c => c.Slot == TimeOfDay.Night), c => Assert.Null(WorldMoves.Where("save", Schedule, Places, c)));
    }

    [Fact]
    public void The_same_save_and_slot_always_give_the_same_answer()
    {
        var clock = new ClockState(9, TimeOfDay.Afternoon);

        Assert.Equal(WorldMoves.Where("save", Schedule, Places, clock), WorldMoves.Where("save", Schedule, Places, clock));
        Assert.Equal(
            WorldMoves.MeetsByChance("save", "chance", "park", "park", clock),
            WorldMoves.MeetsByChance("save", "chance", "park", "park", clock));
    }

    [Fact]
    public void A_chance_meeting_is_likelier_where_someone_is_and_never_on_the_first_day()
    {
        var clocks = Month().Where(c => c.Day >= WorldMoves.FirstChanceDay).ToList();
        var here = clocks.Count(c => WorldMoves.MeetsByChance("save", "chance", "park", "park", c));
        var elsewhere = clocks.Count(c => WorldMoves.MeetsByChance("save", "chance", "bar", "park", c));

        Assert.True(here > elsewhere, $"here {here}, elsewhere {elsewhere}");
        Assert.InRange(elsewhere, 0, clocks.Count / 8);
        Assert.DoesNotContain(new[] { TimeOfDay.Morning, TimeOfDay.Evening }, slot => WorldMoves.MeetsByChance("save", "chance", "park", "park", new ClockState(1, slot)));
    }
}
