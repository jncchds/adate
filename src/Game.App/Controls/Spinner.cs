using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace Game.App.Controls;

/// <summary>
/// A turning arc wherever a picture or words are on their way. Each spinner has its own rotation, which
/// the style animates by angle: Avalonia has no animator for a whole render transform.
/// </summary>
public class Spinner : Arc
{
    public Spinner() => RenderTransform = new RotateTransform();
}
