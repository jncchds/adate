using Game.Core.Scenes;
using Game.Core.Story;
using Game.Core.World;

namespace Game.Core.Tests;

public class InitiativeTests
{
    private static RouteStatus Status(RelationshipStage stage = RelationshipStage.Friend, int? lastSeen = 3, string? left = null) =>
        new("main_li", true, left, RelationshipState.Start with { Stage = stage }, new Dictionary<string, string>(), lastSeen, 0);

    [Fact]
    public void Nobody_seeks_out_a_player_they_saw_today_or_never_met_or_who_is_gone()
    {
        Assert.Equal(0, Initiative.Chance(Status(lastSeen: 6), 1, 1, today: 6));
        Assert.Equal(0, Initiative.Chance(Status(lastSeen: null), 1, 1, today: 6));
        Assert.Equal(0, Initiative.Chance(Status(left: "Neglect"), 1, 1, today: 6));
    }

    [Fact]
    public void Bolder_tempers_and_longer_absences_raise_the_chance_up_to_the_cap()
    {
        var shy = Initiative.Chance(Status(), 0.6, 1, today: 5);
        var bold = Initiative.Chance(Status(), 1.6, 1, today: 5);
        var later = Initiative.Chance(Status(), 1.6, 1, today: 9);

        Assert.True(bold > shy);
        Assert.True(later > bold);
        Assert.Equal(Initiative.MaxChance, Initiative.Chance(Status(), 5, 0, today: 30));
    }

    [Fact]
    public void A_player_who_never_asks_leaves_room_and_one_who_always_leads_does_not()
    {
        var never = Initiative.Chance(Status(), 1, 0, today: 5);
        var sometimes = Initiative.Chance(Status(), 1, 1, today: 5);
        var leading = Initiative.Chance(Status(), 1, Initiative.LeadingInvites, today: 5);

        Assert.True(never > sometimes);
        Assert.True(sometimes > leading);
        Assert.True(Initiative.Chance(Status(RelationshipStage.Acquaintance), 1, 1, today: 5) < sometimes);
    }

    [Fact]
    public void The_roll_is_deterministic_and_cools_down()
    {
        var clock = new ClockState(5, TimeOfDay.Afternoon);
        Assert.Equal(Initiative.Rolls("save", "main_li", clock, 0.4), Initiative.Rolls("save", "main_li", clock, 0.4));
        Assert.False(Initiative.Rolls("save", "main_li", clock, 0));
        Assert.True(Initiative.Rolls("save", "main_li", clock, 1));

        var hits = Enumerable.Range(1, 400).Count(d => Initiative.Rolls("save", "main_li", new ClockState(d, TimeOfDay.Morning), 0.3));
        Assert.InRange(hits, 80, 160);

        Assert.True(Initiative.CoolingDown("4", 6));
        Assert.False(Initiative.CoolingDown("3", 6));
        Assert.False(Initiative.CoolingDown(null, 6));
    }
}
