using Avalonia;
using Avalonia.Controls;

namespace Game.App.Controls;

/// <summary>Where the stage, the people and the words go.</summary>
/// <param name="Stage">The picture: the whole area in landscape, the top in portrait.</param>
/// <param name="Figure">Where the person stands, or the first of two.</param>
/// <param name="Side">The words and choices, which scroll on their own.</param>
/// <param name="Stacked">Portrait: the words sit under the picture instead of over it.</param>
/// <param name="Companion">Where the second of two people stands; empty with one.</param>
public readonly record struct StageRegions(Rect Stage, Rect Figure, Rect Side, bool Stacked, Rect Companion = default);

/// <summary>
/// Keeps the picture on screen whatever the shape of the window. Children, in order: the stage, the
/// figure, the companion and the side; any further children are overlays across the whole area, drawn above
/// the rest (the back button, the caption), so nobody standing there covers them.
/// </summary>
/// <remarks>
/// <para>
/// Landscape (desktop 16:9, tablets at 4:3 or 16:10, phones on their side at about 20:9) is laid out
/// like a visual novel: the background fills the screen, the person stands at the left, and the
/// words sit in a translucent box over the lower part of the rest. The box grows to nearly the full
/// height when <see cref="Tall"/> is set, for screens with no one standing there (the map, endings).
/// With <see cref="Pair"/>, two people stand at the left and right edges, a little narrower, and the
/// box sits between them.
/// </para>
/// <para>
/// Portrait (phones at about 9:20, tablets upright) stacks them: the picture and the person take the
/// top, up to a square, so a standing person still reads at phone size, and the words scroll below.
/// A box over the lower half of a tall narrow screen would cover the person. Two people share the top,
/// one from each side, overlapping a little in the middle.
/// </para>
/// </remarks>
public sealed class StageLayout : Panel
{
    /// <summary>The person's share of a landscape width.</summary>
    public const double FigureShare = 0.36;

    /// <summary>Each person's share of a landscape width when two stand there: narrower, so the box between them still reads.</summary>
    public const double PairFigureShare = 0.27;

    /// <summary>Each person's share of a portrait width when two stand there; together more than the width, so they overlap.</summary>
    public const double StackedPairShare = 0.6;

    /// <summary>The person's column is never wider than this share of the height, or they stand in empty space.</summary>
    public const double MaxFigureWidthPerHeight = 0.8;

    public const double MinFigureWidth = 220;

    /// <summary>Space between the box and the screen's edges.</summary>
    public const double Inset = 16;

    /// <summary>The box's share of a landscape height during a scene.</summary>
    public const double BoxShare = 0.5;

    /// <summary>Shorter than this and a choice barely fits; short phones on their side get at least this.</summary>
    public const double MinBoxHeight = 240;

    /// <summary>Wider than this and lines get too long to read comfortably.</summary>
    public const double MaxBoxWidth = 880;

    /// <summary>The picture's share of a portrait height.</summary>
    public const double StackedStageShare = 0.45;

    public static readonly StyledProperty<bool> TallProperty =
        AvaloniaProperty.Register<StageLayout, bool>(nameof(Tall));

    public static readonly StyledProperty<bool> FullStageProperty =
        AvaloniaProperty.Register<StageLayout, bool>(nameof(FullStage));

    public static readonly StyledProperty<bool> PairProperty =
        AvaloniaProperty.Register<StageLayout, bool>(nameof(Pair));

    static StageLayout() => AffectsMeasure<StageLayout>(TallProperty, FullStageProperty, PairProperty);

    /// <summary>Whether the words may take nearly the full height: set when no one stands on the stage.</summary>
    public bool Tall
    {
        get => GetValue(TallProperty);
        set => SetValue(TallProperty, value);
    }

    /// <summary>
    /// Whether the picture fills the whole area upright too: set when an overlay stands in for the words, such as the
    /// phone of a conversation by text (user feedback: the background stays fullscreen, as on every other screen).
    /// </summary>
    public bool FullStage
    {
        get => GetValue(FullStageProperty);
        set => SetValue(FullStageProperty, value);
    }

    /// <summary>Whether two people stand on the stage, so the companion gets a place of their own.</summary>
    public bool Pair
    {
        get => GetValue(PairProperty);
        set => SetValue(PairProperty, value);
    }

    public bool IsStacked { get; private set; }

    public static StageRegions Split(Size size, bool tall, bool fullStage = false, bool pair = false)
    {
        var width = size.Width;
        var height = size.Height;

        if (width >= height)
        {
            var boxHeight = BoxHeight(height, tall);

            if (pair)
            {
                var eachWidth = Math.Min(Math.Max(MinFigureWidth, Math.Min(width * PairFigureShare, height * MaxFigureWidthPerHeight)), width / 3);

                // The box is centred between the two, and capped for line length.
                var between = Math.Max(0, width - (2 * eachWidth));
                var pairBoxWidth = Math.Min(between, MaxBoxWidth);

                return new StageRegions(
                    new Rect(size),
                    new Rect(0, 0, eachWidth, height),
                    new Rect(eachWidth + ((between - pairBoxWidth) / 2), height - Inset - boxHeight, pairBoxWidth, boxHeight),
                    false,
                    new Rect(width - eachWidth, 0, eachWidth, height));
            }

            var figureWidth = Math.Min(Math.Max(MinFigureWidth, Math.Min(width * FigureShare, height * MaxFigureWidthPerHeight)), width / 2);

            // The box is centred in what is left beside the person, and capped for line length.
            var room = Math.Max(0, width - figureWidth - Inset);
            var boxWidth = Math.Min(room, MaxBoxWidth);

            return new StageRegions(
                new Rect(size),
                new Rect(0, 0, figureWidth, height),
                new Rect(figureWidth + ((room - boxWidth) / 2), height - Inset - boxHeight, boxWidth, boxHeight),
                false);
        }

        var stageHeight = Math.Min(height * StackedStageShare, width);
        var stage = fullStage ? new Rect(size) : new Rect(0, 0, width, stageHeight);
        var side = new Rect(0, stageHeight, width, height - stageHeight);
        if (!pair)
        {
            return new StageRegions(stage, stage, side, true);
        }

        var share = stage.Width * StackedPairShare;
        return new StageRegions(
            stage,
            new Rect(stage.X, stage.Y, share, stage.Height),
            side,
            true,
            new Rect(stage.Right - share, stage.Y, share, stage.Height));
    }

    private static double BoxHeight(double height, bool tall) =>
        Math.Max(0, tall ? height - (2 * Inset) : Math.Max(height * BoxShare, Math.Min(height - (2 * Inset), MinBoxHeight)));

    /// <summary>The children's places in order; with one person the companion has no room at all.</summary>
    private static Rect[] Rects(StageRegions regions) => [regions.Stage, regions.Figure, regions.Companion, regions.Side];

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size(
            double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);

        var rects = Rects(Split(size, Tall, FullStage, Pair));
        for (var i = 0; i < Children.Count; i++)
        {
            Children[i].Measure(i < rects.Length ? rects[i].Size : size);
        }

        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var regions = Split(finalSize, Tall, FullStage, Pair);
        var rects = Rects(regions);
        for (var i = 0; i < Children.Count; i++)
        {
            Children[i].Arrange(i < rects.Length ? rects[i] : new Rect(finalSize));
        }

        IsStacked = regions.Stacked;
        PseudoClasses.Set(":stacked", regions.Stacked);
        return finalSize;
    }
}
