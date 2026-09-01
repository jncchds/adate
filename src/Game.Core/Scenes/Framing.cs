namespace Game.Core.Scenes;

/// <summary>
/// How much of the character the sprite shows. Drives target resolution and the
/// framing tokens the prompt compiler emits.
/// </summary>
public enum Framing
{
    Portrait = 0,
    Bust = 1,
    HalfBody = 2,
    FullBody = 3,
}
