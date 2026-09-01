using Game.Core.Saves;

namespace Game.Core.Characters;

/// <summary>
/// A character as persisted. Mirrors the <c>character</c> table.
/// </summary>
/// <param name="AnchorImageHash">
/// Content hash of the approved portrait. Every sprite for this character is generated
/// with this image as the IP-Adapter reference, which is what makes the six expressions
/// read as one person.
/// </param>
/// <param name="LoraPath">
/// Reserved. Populated only if the fallback ladder reaches step 4 (per-character LoRA
/// trained from approved sprites). Unused in Spike 0.
/// </param>
public sealed record CharacterRecord(
    Guid Id,
    SaveId SaveId,
    CharacterAppearance Appearance,
    string? AnchorImageHash,
    long? AnchorSeed,
    string? LoraPath)
{
    public int Age => Appearance.Age;
}
