using Game.Core.Story;

namespace Game.Core.Tests;

public class ReactionPopupTests
{
    private static readonly RelationshipState Warm = RelationshipState.Start with { Affection = 10, Trust = 5 };

    [Fact]
    public void Small_changes_stay_hidden()
    {
        Assert.Null(ReactionPopup.For("Maya", Warm, Warm with { Affection = 13 }));
        Assert.Null(ReactionPopup.For("Maya", Warm, Warm with { Trust = 2 }));
    }

    [Fact]
    public void A_considerable_change_either_way_is_shown()
    {
        Assert.Equal("Maya liked that.", ReactionPopup.For("Maya", Warm, Warm with { Affection = 10 + ReactionPopup.Considerable }));
        Assert.Equal("Maya didn't like that.", ReactionPopup.For("Maya", Warm, Warm with { Trust = 5 - ReactionPopup.Considerable - 2 }));
    }

    [Fact]
    public void A_newly_tripped_dealbreaker_always_shows()
    {
        Assert.Equal("Maya won't forget that.", ReactionPopup.For("Maya", Warm, Warm with { Dealbreaker = true }));
        Assert.Null(ReactionPopup.For("Maya", Warm with { Dealbreaker = true }, Warm with { Dealbreaker = true, Affection = 11 }));
    }
}
