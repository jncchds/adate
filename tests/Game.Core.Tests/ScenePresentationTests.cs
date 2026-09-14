using Game.Core.Story;

namespace Game.Core.Tests;

public class ScenePresentationTests
{
    private static readonly string[] Slots = ["neutral", "smile", "laughing", "sad", "angry", "surprised"];

    [Fact]
    public void The_written_expression_wins_when_the_pack_can_draw_it()
    {
        Assert.Equal("sad", ScenePresentation.Expression("Sad", "smile", Slots));
    }

    [Fact]
    public void An_expression_the_pack_cannot_draw_falls_back_to_resting_then_to_the_first_slot()
    {
        Assert.Equal("smile", ScenePresentation.Expression("wistful", "smile", Slots));
        Assert.Equal("smile", ScenePresentation.Expression(null, " smile ", Slots));
        Assert.Equal("neutral", ScenePresentation.Expression("wistful", "pensive", Slots));
    }
}
