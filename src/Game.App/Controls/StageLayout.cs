using Avalonia;
using Avalonia.Controls;

namespace Game.App.Controls;

/// <summary>
/// Keeps the picture on screen whatever the shape of the window, with the words beside or below it.
/// The first child is the stage and never scrolls; the second is the side, which scrolls on its own.
/// </summary>
/// <remarks>
/// <para>
/// Landscape (desktop 16:9, tablets at 4:3 or 16:10, phones on their side at about 20:9) puts the side
/// on the right at a readable width and gives the stage the rest.
/// </para>
/// <para>
/// Portrait (phones at about 9:20, tablets upright) stacks them: the stage takes the top, up to a
/// square, so a standing person still reads at phone size, and the side scrolls underneath.
/// </para>
/// </remarks>
public sealed class StageLayout : Panel
{
    /// <summary>The side's share of a landscape width, before the limits below.</summary>
    public const double SideShare = 0.36;

    /// <summary>Narrower than this and choices wrap into unreadable columns.</summary>
    public const double MinSideWidth = 320;

    /// <summary>Wider than this and lines get too long to read comfortably.</summary>
    public const double MaxSideWidth = 560;

    /// <summary>The stage's share of a portrait height.</summary>
    public const double StackedStageShare = 0.45;

    /// <summary>Where the stage and the side go in an area of <paramref name="size"/>.</summary>
    public static (Rect Stage, Rect Side, bool Stacked) Split(Size size)
    {
        var width = size.Width;
        var height = size.Height;

        if (width >= height)
        {
            // Never more than half: in a nearly square window the picture keeps at least as much room.
            var side = Math.Min(Math.Clamp(width * SideShare, MinSideWidth, MaxSideWidth), width / 2);
            var stage = width - side;
            return (new Rect(0, 0, stage, height), new Rect(stage, 0, side, height), false);
        }

        var stageHeight = Math.Min(height * StackedStageShare, width);
        return (new Rect(0, 0, width, stageHeight), new Rect(0, stageHeight, width, height - stageHeight), true);
    }

    public bool IsStacked { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size(
            double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);

        var (stage, side, _) = Split(size);
        if (Children.Count > 0)
        {
            Children[0].Measure(stage.Size);
        }

        if (Children.Count > 1)
        {
            Children[1].Measure(side.Size);
        }

        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (stage, side, stacked) = Split(finalSize);
        if (Children.Count > 0)
        {
            Children[0].Arrange(stage);
        }

        if (Children.Count > 1)
        {
            Children[1].Arrange(side);
        }

        IsStacked = stacked;
        PseudoClasses.Set(":stacked", stacked);
        return finalSize;
    }
}
