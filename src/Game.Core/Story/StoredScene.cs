using Game.Core.World;

namespace Game.Core.Story;

/// <summary>
/// A turn's scene as the player saw it, saved as it happens: the place's picture, the person, the
/// words, the player's reply and the reaction. The newest one not closed is the scene the player is in.
/// </summary>
/// <param name="OutcomeJson">The turn as decided, so a scene whose writing was interrupted can be finished.</param>
/// <param name="Written">Whether <paramref name="Text"/> is the finished scene rather than the turn's authored text.</param>
/// <param name="BackgroundPath">The place's picture, once drawn; the same stored image path every frontend uses.</param>
/// <param name="SpritePath">The person as last shown, once drawn.</param>
/// <param name="Reply">What the player chose or typed.</param>
public sealed record StoredScene(
    long Id,
    ClockState Clock,
    string PlaceId,
    string? EncounterId,
    string OutcomeJson,
    bool Written,
    string Text,
    string? BackgroundPath,
    Guid? CharacterId,
    string? Speaker,
    string? Expression,
    string? SpritePath,
    string? Reply,
    string? Reaction,
    string? Popup,
    string? Agreed,
    bool Closed);
