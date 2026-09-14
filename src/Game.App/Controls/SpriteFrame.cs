using Avalonia;
using Avalonia.Controls;

namespace Game.App.Controls;

/// <summary>
/// Frames a full-body sprite from the head down to about mid-thigh, centred, whatever the area's
/// shape. A whole standing figure in a short stage or a small card leaves the face too small to read.
/// </summary>
public sealed class SpriteFrame : Panel
{
    /// <summary>Scene sprites are rendered at 768x1152.</summary>
    public const double SpriteAspect = 768.0 / 1152.0;

    /// <summary>How much of the sprite's height shows.</summary>
    public const double VisibleShare = 0.62;

    /// <summary>Headroom above the hair, as a share of the sprite's height, cropped away.</summary>
    public const double TopCrop = 0.03;

    /// <summary>How far past the area's sides the figure may reach, so arms are cut before the face shrinks.</summary>
    public const double WidthAllowance = 1.15;

    /// <summary>Margin below the feet, as a share of the sprite's height, cropped away when the whole figure fits.</summary>
    public const double FootCrop = 0.03;

    /// <summary>Where the sprite goes in an area of <paramref name="area"/>; it may overhang and is clipped.</summary>
    /// <remarks>
    /// Normally the head is at the top and the legs run off the bottom. In a column narrow for its height
    /// (a tablet at 4:3) the whole figure fits with room to spare; it then stands on the bottom edge
    /// rather than floating at the top.
    /// </remarks>
    public static Rect Frame(Size area)
    {
        var height = area.Height / VisibleShare;
        var width = height * SpriteAspect;

        var widest = area.Width * WidthAllowance;
        if (width > widest)
        {
            width = widest;
            height = width / SpriteAspect;
        }

        var top = Math.Max(-height * TopCrop, area.Height - (height * (1 - FootCrop)));
        return new Rect((area.Width - width) / 2, top, width, height);
    }

    public SpriteFrame() => ClipToBounds = true;

    protected override Size MeasureOverride(Size availableSize)
    {
        var finite = new Size(
            double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);

        foreach (var child in Children)
        {
            child.Measure(Frame(finite).Size);
        }

        // Takes whatever it is given rather than asking for room: the frame fills its container.
        return default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var frame = Frame(finalSize);
        foreach (var child in Children)
        {
            child.Arrange(frame);
        }

        return finalSize;
    }
}
