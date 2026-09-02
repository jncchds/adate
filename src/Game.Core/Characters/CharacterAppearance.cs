namespace Game.Core.Characters;

/// <summary>
/// The declared, player-authored appearance of a character. This is the contract the
/// generated art is judged against: HANDOFF 2 requires every one of these attributes to
/// survive across all sprites, and hair colour is the documented first thing to drift.
/// </summary>
/// <param name="Subject">
/// Which subject anchor this character uses, resolved against the style pack's
/// <see cref="Style.StylePack.Subjects"/>. A key rather than an enum because a game
/// decides which subjects its love interests may take, and the tokens behind each key are
/// checkpoint-specific. Not inferred from any other attribute.
/// </param>
/// <param name="Age">
/// HANDOFF 1.9: an explicit adult age, injected into every prompt. Tag-based checkpoints
/// associate terms like "petite" and "youthful" with juvenile features and drift without
/// an anchor. Validated by <see cref="Validate"/>, never inferred.
/// </param>
public sealed record CharacterAppearance(
    string Subject,
    int Age,
    string EyeColor,
    string HairColor,
    string HairStyle,
    string SkinTone,
    string Build,
    string Height,
    string DistinguishingFeature)
{
    public const int MinimumAge = 18;

    /// <summary>Throws if the record could not legally or usefully be sent to a generator.</summary>
    public void Validate()
    {
        if (Age < MinimumAge)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Age), Age, $"Character age must be at least {MinimumAge}.");
        }

        // A blank subject would reach the pack lookup and fail there with a less useful
        // message. It is also what a save written before subjects existed deserialises to.
        Require(Subject);
        Require(EyeColor);
        Require(HairColor);
        Require(HairStyle);
        Require(SkinTone);
        Require(Build);
        Require(Height);
        // DistinguishingFeature is the one optional attribute; absence is meaningful.

        static void Require(string value, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? name = null)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException($"Appearance attribute '{name}' must not be blank.", name);
            }
        }
    }
}
