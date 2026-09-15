using Game.Core.Content;
using Game.Core.Scenes;
using Game.Core.Story;
using Game.Core.World;

namespace Game.Core.Tests;

public class HappeningTests
{
    private static string ContentPath(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.EnumerateFiles("*.slnx").Any() is false)
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("No repository root."), "content", file);
    }

    private static readonly HappeningContent Content = HappeningContent.Load(ContentPath("happenings.json"));

    private static IEnumerable<ClockState> Month() =>
        Enumerable.Range(1, 28).SelectMany(day => new[] { TimeOfDay.Morning, TimeOfDay.Midday, TimeOfDay.Afternoon, TimeOfDay.Evening, TimeOfDay.Night }
            .Select(slot => new ClockState(day, slot)));

    [Fact]
    public void Every_place_type_has_happenings_and_every_name_in_the_file_exists()
    {
        var types = new JsonLocationCatalog(ContentPath("place-types.json")).All().Select(t => t.Id).ToList();

        Content.ValidateAgainst(types, WeatherContent.Load(ContentPath("weather.json")).Kinds.Select(k => k.Id));
        Assert.All(types, type => Assert.True(Content.Places.ContainsKey(type), $"No happenings for {type}."));
        Assert.Throws<InvalidOperationException>(() => Content.ValidateAgainst(["cafe"], ["clear"]));
    }

    [Fact]
    public void A_pick_is_stable_for_the_same_slot_leaves_some_moments_quiet_and_keeps_to_its_times()
    {
        var clocks = Month().ToList();
        var picks = clocks.Select(c => Content.Pick("save", "lake-pier", "clear", c)).ToList();

        Assert.Equal(picks, clocks.Select(c => Content.Pick("save", "lake-pier", "clear", c)));
        Assert.Contains(picks, p => p is null);
        Assert.True(picks.Distinct().Count() > 3);

        var lanterns = Content.Places["lake-pier"].Single(h => h.Text.Contains("lanterns", StringComparison.Ordinal));
        Assert.All(
            clocks.Where((c, i) => picks[i] == lanterns.Text),
            c => Assert.Contains(c.Slot, lanterns.Slots!));
    }
}
