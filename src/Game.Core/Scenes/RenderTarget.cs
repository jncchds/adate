namespace Game.Core.Scenes;

/// <summary>
/// What kind of image is being compiled for. Not part of the HANDOFF 4 signature, but the
/// compiler cannot be correct without it: a sprite must carry no background tokens (it is
/// matted to alpha and composited over one), and a background must carry no character
/// tokens. Passing the same intent for both and hoping is how "cutout on a photo" happens.
/// </summary>
public enum RenderTarget
{
    /// <summary>Candidate portrait used to choose and anchor a character's look.</summary>
    Portrait = 0,

    /// <summary>Transparent-background character sprite, generated from the anchor.</summary>
    Sprite = 1,

    /// <summary>Empty location background, no characters.</summary>
    Background = 2,
}
