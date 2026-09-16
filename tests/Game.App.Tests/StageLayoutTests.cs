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

    [Theory]
    [InlineData(390, 866)]
    [InlineData(1280, 720)]
    public void Behind_a_phone_the_picture_fills_the_screen_whatever_its_shape(double width, double height)
    {
        var regions = StageLayout.Split(new Size(width, height), tall: false, fullStage: true);

        Assert.Equal(new Rect(0, 0, width, height), regions.Stage);
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

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void Children_after_the_side_overlay_the_whole_area()
    {
        var layout = new StageLayout();
        Avalonia.Controls.Border[] children = [new(), new(), new(), new(), new()];
        layout.Children.AddRange(children);

        layout.Measure(new Size(1280, 720));
        layout.Arrange(new Rect(0, 0, 1280, 720));

        // Layout rounds to whole pixels, so the side is compared within one.
        var regions = StageLayout.Split(new Size(1280, 720), tall: false);
        Assert.Equal(regions.Side.X, children[3].Bounds.X, 0.51);
        Assert.Equal(regions.Side.Y, children[3].Bounds.Y, 0.51);
        Assert.Equal(regions.Side.Width, children[3].Bounds.Width, 1.01);
        Assert.Equal(regions.Side.Height, children[3].Bounds.Height, 1.01);
        Assert.Equal(0, children[2].Bounds.Width);
        Assert.Equal(new Rect(0, 0, 1280, 720), children[4].Bounds);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1280, 720)]
    [InlineData(1024, 768)]
    [InlineData(844, 390)]
    [InlineData(390, 844)]
    [InlineData(768, 1024)]
    public void One_person_stands_exactly_where_they_did_before_two_could(double width, double height)
    {
        foreach (var tall in new[] { false, true })
        {
            var one = StageLayout.Split(new Size(width, height), tall);

            Assert.Equal(default, one.Companion);
            Assert.Equal(one, StageLayout.Split(new Size(width, height), tall, pair: false));
        }
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1280, 720)]
    [InlineData(1280, 800)]
    [InlineData(1024, 768)]
    [InlineData(844, 390)]
    public void Two_people_stand_at_either_edge_with_the_words_between_them(double width, double height)
    {
        var regions = StageLayout.Split(new Size(width, height), tall: false, pair: true);
        var screen = new Rect(0, 0, width, height);

        Assert.Equal(0, regions.Figure.Left);
        Assert.Equal(width, regions.Companion.Right, 3);
        Assert.False(regions.Figure.Intersects(regions.Companion), "The two stand apart.");
        Assert.True(screen.Contains(regions.Side));
        Assert.False(regions.Side.Intersects(regions.Figure) || regions.Side.Intersects(regions.Companion), "The words cover neither.");
        Assert.True(regions.Side.Height >= StageLayout.MinBoxHeight);
        Assert.True(regions.Side.Width >= Math.Min(width, height) * 0.5, "The box is wide enough to read.");
        Assert.True(regions.Figure.Width >= StageLayout.MinFigureWidth);
    }

    [Fact]
    public void Two_people_on_an_upright_phone_share_the_picture_from_either_side()
    {
        var regions = StageLayout.Split(new Size(390, 866), tall: false, pair: true);

        Assert.True(regions.Stacked);
        Assert.Equal((0.0, 390.0), (regions.Figure.Left, regions.Companion.Right));
        Assert.True(regions.Stage.Contains(regions.Figure) && regions.Stage.Contains(regions.Companion));
        Assert.True(regions.Figure.Right > regions.Companion.Left, "They overlap a little in the middle.");
        Assert.Equal(regions.Stage.Bottom, regions.Side.Top);
    }

    [Fact]
    public void A_person_on_a_desktop_stage_is_cut_off_at_the_knees()
    {
        var regions = StageLayout.Split(new Size(1280, 720), tall: false);
        var frame = SpriteFrame.Frame(regions.Figure.Size);

        Assert.Equal(720 / SpriteFrame.VisibleShare, frame.Height, 3);
        Assert.True(frame.Top < 0 && frame.Bottom > 720, "Headroom is cropped above and the legs below the knees.");
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
    public void A_figure_that_fits_whole_stands_on_the_bottom_edge()
    {
        // A column so narrow for its height that the whole figure fits once the width limits it.
        var area = new Size(200, 768);
        var frame = SpriteFrame.Frame(area);

        Assert.True(frame.Height < area.Height / SpriteFrame.VisibleShare, "The column limits the figure by width.");
        Assert.Equal(area.Height, frame.Top + (frame.Height * (1 - SpriteFrame.FootCrop)), 3);
        Assert.True(frame.Top > 0, "No headroom is cropped when the figure stands at the bottom.");
    }

    [Fact]
    public void A_narrow_column_crops_the_arms_before_shrinking_the_face_further()
    {
        var frame = SpriteFrame.Frame(new Size(60, 160));

        Assert.Equal(60 * SpriteFrame.WidthAllowance, frame.Width, 3);
        Assert.Equal(frame.Width / SpriteFrame.SpriteAspect, frame.Height, 3);
    }
}
