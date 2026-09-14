using Avalonia;
using Game.App.Controls;

namespace Game.App.Tests;

public class StageLayoutTests
{
    [Fact]
    public void Desktop_fills_the_screen_with_the_person_left_and_the_words_in_a_box_below_right()
    {
        var regions = StageLayout.Split(new Size(1280, 720), tall: false);

        Assert.False(regions.Stacked);
        Assert.Equal(new Rect(0, 0, 1280, 720), regions.Stage);
        Assert.Equal(1280 * StageLayout.FigureShare, regions.Figure.Width, 3);
        Assert.Equal(720 * StageLayout.BoxShare, regions.Side.Height, 3);
        Assert.Equal(720 - StageLayout.Inset, regions.Side.Bottom, 3);
        Assert.Equal(1280 - StageLayout.Inset, regions.Side.Right, 3);
    }

    [Fact]
    public void A_wide_monitor_caps_the_box_for_line_length_and_centres_it_beside_the_person()
    {
        var regions = StageLayout.Split(new Size(1920, 1080), tall: false);

        Assert.Equal(StageLayout.MaxBoxWidth, regions.Side.Width);
        var room = 1920 - regions.Figure.Width - StageLayout.Inset;
        Assert.Equal(regions.Figure.Right + ((room - StageLayout.MaxBoxWidth) / 2), regions.Side.Left, 3);
    }

    [Fact]
    public void A_phone_on_its_side_gives_the_box_at_least_its_minimum_height()
    {
        var regions = StageLayout.Split(new Size(844, 390), tall: false);

        Assert.False(regions.Stacked);
        Assert.Equal(StageLayout.MinBoxHeight, regions.Side.Height);
        Assert.Equal(844 * StageLayout.FigureShare, regions.Figure.Width, 3);
        Assert.True(regions.Figure.Width <= 390 * StageLayout.MaxFigureWidthPerHeight);
    }

    [Fact]
    public void With_no_one_on_stage_the_box_takes_nearly_the_full_height()
    {
        var regions = StageLayout.Split(new Size(1280, 720), tall: true);

        Assert.Equal(StageLayout.Inset, regions.Side.Top, 3);
        Assert.Equal(720 - StageLayout.Inset, regions.Side.Bottom, 3);
    }

    [Fact]
    public void An_upright_phone_stacks_the_words_under_a_square_capped_picture()
    {
        var regions = StageLayout.Split(new Size(390, 866), tall: false);

        Assert.True(regions.Stacked);
        Assert.Equal(regions.Stage, regions.Figure);
        Assert.Equal(866 * StageLayout.StackedStageShare, regions.Stage.Height, 3);
        Assert.Equal(regions.Stage.Bottom, regions.Side.Top);
        Assert.Equal(866, regions.Side.Bottom, 3);
    }

    [Fact]
    public void An_upright_tablet_gives_the_picture_less_than_half()
    {
        var regions = StageLayout.Split(new Size(768, 1024), tall: true);

        Assert.True(regions.Stacked);
        Assert.Equal(1024 * StageLayout.StackedStageShare, regions.Stage.Height, 3);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1280, 720)]
    [InlineData(1280, 800)]
    [InlineData(1024, 768)]
    [InlineData(844, 390)]
    [InlineData(600, 600)]
    [InlineData(390, 844)]
    [InlineData(412, 915)]
    [InlineData(768, 1024)]
    public void The_words_stay_on_screen_and_never_cover_the_person(double width, double height)
    {
        foreach (var tall in new[] { false, true })
        {
            var regions = StageLayout.Split(new Size(width, height), tall);
            var screen = new Rect(0, 0, width, height);

            Assert.True(screen.Contains(regions.Side), $"The box fits the screen (tall: {tall}).");
            Assert.True(regions.Side.Width >= Math.Min(width, height) * 0.5, "The box is wide enough to read.");
            Assert.True(regions.Stacked ? !regions.Stage.Intersects(regions.Side) : !regions.Figure.Intersects(regions.Side));
        }
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
    public void A_narrow_column_crops_the_arms_before_shrinking_the_face_further()
    {
        var frame = SpriteFrame.Frame(new Size(116, 160));

        Assert.Equal(116 * SpriteFrame.WidthAllowance, frame.Width, 3);
        Assert.Equal(frame.Width / SpriteFrame.SpriteAspect, frame.Height, 3);
    }
}
