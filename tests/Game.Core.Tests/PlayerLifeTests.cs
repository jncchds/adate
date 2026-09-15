using Game.Core.Content;
using Game.Core.Scenes;
using Game.Core.Settings;
using Game.Core.Story;
using Game.Core.World;

namespace Game.Core.Tests;

public class PlayerLifeTests
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

    private static StoryContent Story() =>
        StoryContent.Load(ContentPath("values.json"), ContentPath("predicates.json"), ContentPath("relationship.json"));

    private static readonly PlayerJob Job = new(
        "a junior analyst", "office", [TimeOfDay.Midday, TimeOfDay.Afternoon], [0, 1, 2, 3, 4], "ambition", "work a shift", "works as a junior analyst");

    [Fact]
    public void What_the_player_does_builds_traits_and_a_missed_shift_costs_stability()
    {
        var flags = new Dictionary<string, string> { ["player.trait.stability"] = "1" };
        var toSet = new Dictionary<string, string>();

        PlayerLife.Show(toSet, flags, ["adventure", "adventure"]);
        PlayerLife.Show(toSet, flags, ["adventure", "kindness"]);
        PlayerLife.WorkShift(toSet, flags, Job);
        PlayerLife.MissShift(toSet, flags);
        PlayerLife.MissShift(toSet, flags);

        Assert.Equal("2", toSet["player.trait.adventure"]);
        Assert.Equal("1", toSet["player.trait.kindness"]);
        Assert.Equal("1", toSet["player.trait.ambition"]);
        Assert.Equal("1", toSet[PlayerLife.ShiftsWorkedKey]);
        Assert.Equal("2", toSet[PlayerLife.ShiftsMissedKey]);
        Assert.Equal("0", toSet["player.trait.stability"]);
        Assert.Equal(2, PlayerLife.Traits(toSet)["adventure"]);
    }

    [Fact]
    public void A_shift_runs_on_its_slots_and_weekdays()
    {
        Assert.True(PlayerLife.OnShift(Job, new ClockState(1, TimeOfDay.Midday)));
        Assert.False(PlayerLife.OnShift(Job, new ClockState(1, TimeOfDay.Morning)));
        Assert.False(PlayerLife.OnShift(Job, new ClockState(6, TimeOfDay.Midday)), "Day 6 is weekday 5, a day off.");
        Assert.True(PlayerLife.OnShift(Job, new ClockState(8, TimeOfDay.Afternoon)));
        Assert.False(PlayerLife.OnShift(null, new ClockState(1, TimeOfDay.Midday)));
    }

    [Fact]
    public void Rapport_grows_with_the_traits_a_character_values_and_is_capped()
    {
        var rules = Story().Rules;
        int Values(string desire) => desire switch { "adventure" => 3, "kindness" => 2, _ => 0 };

        Assert.Equal(0, PlayerLife.Rapport(Values, new Dictionary<string, int> { ["adventure"] = 2 }, rules));
        Assert.Equal(1, PlayerLife.Rapport(Values, new Dictionary<string, int> { ["adventure"] = 3 }, rules));
        Assert.Equal(0, PlayerLife.Rapport(Values, new Dictionary<string, int> { ["humour"] = 20 }, rules));
        Assert.Equal(rules.RapportMax, PlayerLife.Rapport(Values, new Dictionary<string, int> { ["adventure"] = 20, ["kindness"] = 20 }, rules));
    }

    [Fact]
    public void Every_setting_has_a_job_that_shows_a_quality_people_look_for_and_a_home()
    {
        var desires = Story().Values.Desires.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var types = new JsonLocationCatalog(ContentPath("place-types.json"));
        var settings = new JsonSettingCatalog(ContentPath("settings"), types);

        Assert.All(settings.All(), setting =>
        {
            Assert.NotNull(setting.Job);
            Assert.Contains(setting.Job!.Trait, desires);
            Assert.NotNull(setting.Home);
            Assert.Equal(DressCode.Home, types.All().Single(t => t.Id == setting.HomeType).Dress);
        });
    }
}
