using Game.Core.Story;

namespace Game.Core.Tests;

public class DaysAndMoodsTests
{
    [Fact]
    public void A_day_has_the_same_shape_every_time_and_the_shapes_all_come_up()
    {
        var shapes = Enumerable.Range(1, 200).Select(d => DayShapes.For("save", d)).ToList();

        Assert.Equal(shapes, Enumerable.Range(1, 200).Select(d => DayShapes.For("save", d)));
        Assert.All(Enum.GetValues<DayShape>(), shape => Assert.Contains(shape, shapes));
        Assert.True(shapes.Count(s => s == DayShape.Ordinary) is > 70 and < 130);
    }

    [Fact]
    public void Only_an_ordinary_day_says_nothing_and_changes_nothing()
    {
        var ordinary = DayShapes.Effects(DayShape.Ordinary);

        Assert.Equal((1.0, 1.0, 1.0, (string?)null), (ordinary.Wander, ordinary.MeetByChance, ordinary.Initiative, ordinary.Line));
        Assert.All(new[] { DayShape.Quiet, DayShape.Busy, DayShape.Lively }, s => Assert.NotNull(DayShapes.Effects(s).Line));
    }

    [Fact]
    public void Being_away_long_makes_someone_wistful_and_likelier_to_seek_the_player()
    {
        var away = Disposition.For("save", "rin", 9, WistfulAfterDays());
        var recent = Disposition.For("save", "rin", 9, 0);

        Assert.Equal(Mood.Wistful, away.Mood);
        Assert.NotEqual(Mood.Wistful, Mood.Calm);
        Assert.True(away.InitiativeScale > 0.9);
        Assert.Equal(away, Disposition.For("save", "rin", 9, WistfulAfterDays()));
        Assert.NotNull(recent);
    }

    [Fact]
    public void Wanting_company_raises_the_chance_and_wanting_to_be_alone_lowers_it()
    {
        Assert.True(new Disposition(Mood.Calm, Goal.Company).InitiativeScale > 1.0);
        Assert.True(new Disposition(Mood.Calm, Goal.Alone).InitiativeScale < 0.5);
        Assert.True(new Disposition(Mood.Guarded, Goal.Alone).InitiativeScale < new Disposition(Mood.Wistful, Goal.Company).InitiativeScale);
    }

    [Fact]
    public void Every_mood_and_goal_gets_used_and_the_line_asks_for_subtlety()
    {
        var all = Enumerable.Range(1, 400).Select(d => Disposition.For("save", "rin", d, null)).ToList();

        Assert.All(Enum.GetValues<Mood>(), m => Assert.Contains(all, x => x.Mood == m));
        Assert.All(Enum.GetValues<Goal>(), g => Assert.Contains(all, x => x.Goal == g));
        Assert.Contains("do not state it outright", new Disposition(Mood.Restless, Goal.Errand).Line("Rin"));
        Assert.StartsWith("Today Rin is restless", new Disposition(Mood.Restless, Goal.Errand).Line("Rin"));
    }

    private static int WistfulAfterDays() => Disposition.WistfulAfterDays;
}
