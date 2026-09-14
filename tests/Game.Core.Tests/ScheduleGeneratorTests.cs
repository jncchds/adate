using Game.Core.Content;
using Game.Core.Scenes;
using Game.Core.Settings;
using Game.Core.Story;
using Game.Core.World;

namespace Game.Core.Tests;

public class ScheduleGeneratorTests
{
    private static SettingDefinition BigCity()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.EnumerateFiles("*.sln").Concat(dir.EnumerateFiles("*.slnx")).Any() is false)
        {
            dir = dir.Parent;
        }

        var content = Path.Combine(dir!.FullName, "content");
        return new JsonSettingCatalog(Path.Combine(content, "settings"), new JsonLocationCatalog(Path.Combine(content, "place-types.json"))).Get("big-city");
    }

    [Fact]
    public void The_anchor_holds_on_every_weekday_and_nights_are_free()
    {
        var setting = BigCity();

        foreach (var seed in Enumerable.Range(0, 30).Select(i => (long)i * 977))
        {
            var schedule = ScheduleGenerator.For("rin", setting, "corner-cafe", TimeOfDay.Morning, seed);

            foreach (var day in Enumerable.Range(1, 28))
            {
                if (CharacterSchedule.Weekday(day) < 5)
                {
                    Assert.Equal("corner-cafe", schedule.Where(new ClockState(day, TimeOfDay.Morning)));
                }

                Assert.Null(schedule.Where(new ClockState(day, TimeOfDay.Night)));
            }

            Assert.All(schedule.Entries, e => Assert.Contains(setting.Places, p => p.Id == e.PlaceId));
        }
    }

    [Fact]
    public void A_person_gets_the_same_week_from_the_same_seed_and_a_different_one_from_another()
    {
        var setting = BigCity();

        string Week(long seed) => string.Join(",", ScheduleGenerator.For("maya", setting, null, null, seed).Entries
            .Select(e => $"{e.Slot}:{e.PlaceId}:{string.Join("", e.Weekdays ?? [])}"));

        Assert.Equal(Week(5), Week(5));
        Assert.Contains(Enumerable.Range(6, 20), s => Week(s) != Week(5));
    }

    [Fact]
    public void People_are_somewhere_the_player_can_find_them_part_of_the_time()
    {
        var setting = BigCity();
        var busy = Enumerable.Range(0, 200)
            .Select(i => ScheduleGenerator.For("kai", setting, null, null, i))
            .Average(s => s.Entries.Count);

        // Four waking slots, weekdays and weekend each busy about half the time.
        Assert.InRange(busy, 3.0, 6.0);
    }
}
