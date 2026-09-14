using Avalonia;
using Game.App.Controls;

namespace Game.App.Tests;

public class StageLayoutTests
{
    [Fact]
    public void Desktop_puts_the_words_on_the_right_at_their_widest()
    {
        var (stage, side, stacked) = StageLayout.Split(new Size(1920, 1080));

        Assert.False(stacked);
        Assert.Equal(new Rect(0, 0, 1360, 1080), stage);
        Assert.Equal(new Rect(1360, 0, 560, 1080), side);
    }

    [Fact]
    public void A_phone_on_its_side_keeps_a_readable_column()
    {
        var (stage, side, stacked) = StageLayout.Split(new Size(866, 390));

        Assert.False(stacked);
        Assert.Equal(StageLayout.MinSideWidth, side.Width);
        Assert.Equal(546, stage.Width);
    }

    [Fact]
    public void A_tablet_in_landscape_shares_the_width()
    {
        var (stage, side, stacked) = StageLayout.Split(new Size(1024, 768));

        Assert.False(stacked);
        Assert.Equal(1024 * StageLayout.SideShare, side.Width, 3);
        Assert.True(stage.Width > side.Width);
    }

    [Fact]
    public void An_upright_phone_stacks_the_words_under_a_square_stage()
    {
        var (stage, side, stacked) = StageLayout.Split(new Size(390, 866));

        Assert.True(stacked);
        Assert.Equal(390, stage.Width);
        Assert.Equal(866 * StageLayout.StackedStageShare, stage.Height, 3);
        Assert.Equal(stage.Bottom, side.Top);
        Assert.Equal(866, side.Bottom, 3);
    }

    [Fact]
    public void An_upright_tablet_gives_the_stage_less_than_half()
    {
        var (stage, _, stacked) = StageLayout.Split(new Size(768, 1024));

        Assert.True(stacked);
        Assert.Equal(1024 * StageLayout.StackedStageShare, stage.Height, 3);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1280, 720)]
    [InlineData(1280, 800)]
    [InlineData(1024, 768)]
    [InlineData(866, 390)]
    [InlineData(600, 600)]
    [InlineData(390, 866)]
    [InlineData(412, 915)]
    [InlineData(768, 1024)]
    public void The_stage_and_the_words_cover_the_area_without_overlapping(double width, double height)
    {
        var (stage, side, _) = StageLayout.Split(new Size(width, height));

        Assert.False(stage.Intersects(side));
        Assert.Equal(width * height, (stage.Width * stage.Height) + (side.Width * side.Height), 3);
        Assert.True(stage.Width * stage.Height >= width * height * 0.4, "The picture keeps a real share of the screen.");
    }

    [Fact]
    public void A_sprite_is_framed_from_the_head_and_centred()
    {
        var frame = SpriteFrame.Frame(new Size(600, 620));

        Assert.Equal(620 / SpriteFrame.VisibleShare, frame.Height, 3);
        Assert.Equal(600 / 2.0, frame.Center.X, 3);
        Assert.True(frame.Top < 0, "Headroom above the hair is cropped.");
    }

    [Fact]
    public void A_narrow_card_crops_the_arms_before_shrinking_the_face_further()
    {
        var frame = SpriteFrame.Frame(new Size(116, 160));

        Assert.Equal(116 * SpriteFrame.WidthAllowance, frame.Width, 3);
        Assert.Equal(frame.Width / SpriteFrame.SpriteAspect, frame.Height, 3);
    }
}
