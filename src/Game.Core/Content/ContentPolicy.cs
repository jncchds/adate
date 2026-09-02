namespace Game.Core.Content;

/// <summary>
/// The single place that decides what may be depicted or narrated, for one character in one
/// scene. Every render and every narration resolves through here.
/// </summary>
/// <remarks>
/// <para>
/// Two things are configurable per game and one is not. A game chooses its minimum character
/// age (16 for a teen-romance setting, 18 for an adult one) and whether adult content is
/// enabled at all. A game cannot choose whether a character under 18 is clamped to
/// <see cref="Ceiling.PG13"/>: that clamp is computed from the character's own age, has no
/// parameter, and no configuration value reaches it.
/// </para>
/// <para>
/// The clamp lives here rather than in a validation pass because a check that runs somewhere
/// is a check that can be bypassed elsewhere. <see cref="Resolve"/> is the only way to obtain
/// a <see cref="ContentDecision"/>, and a decision is what the generation and narration paths
/// require, so there is no route from a character record to an image that skips it.
/// </para>
/// </remarks>
public static class ContentPolicy
{
    /// <summary>
    /// The age at and above which a game may permit more than <see cref="Ceiling.PG13"/>.
    /// Not configurable, and not the same thing as a game's minimum character age.
    /// </summary>
    public const int AdultAge = 18;

    /// <summary>
    /// The lowest minimum age any game may declare. A game may set its own floor higher.
    /// </summary>
    public const int LowestPermittedMinimumAge = 16;

    /// <summary>
    /// Resolves what this scene may show and how it may be written.
    /// </summary>
    /// <param name="settings">Per-game configuration: age floor and the adult content toggle.</param>
    /// <param name="characterAge">The character's declared age, from their appearance record.</param>
    /// <param name="packCeiling">The highest ceiling the style pack is willing to render.</param>
    /// <param name="requested">
    /// How intimate the scene wants to be. Requesting more than is permitted is not an error:
    /// it is answered with <see cref="ContentDecision.FadeToBlack"/>, because a story that
    /// reaches for a moment it cannot depict should cut away rather than fail.
    /// </param>
    public static ContentDecision Resolve(
        GameContentSettings settings,
        int characterAge,
        Ceiling packCeiling,
        Intimacy requested)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (characterAge < settings.MinimumCharacterAge)
        {
            throw new ArgumentOutOfRangeException(
                nameof(characterAge),
                characterAge,
                $"Character age is below this game's minimum of {settings.MinimumCharacterAge}.");
        }

        // The clamp. A character under 18 is PG13 regardless of what the game, the save or the
        // pack allows, and regardless of what the scene asked for.
        var ageCap = characterAge >= AdultAge ? Ceiling.Explicit : Ceiling.PG13;

        var permitted = Min(Min(settings.MaxCeiling, packCeiling), ageCap);

        // A minor's scene never renders intimacy, and never narrates it in detail either.
        // Fading the image while writing the scene explicitly would defeat the point.
        var isMinor = characterAge < AdultAge;

        return requested switch
        {
            Intimacy.None => new ContentDecision(permitted, Depict: true, Narration.Full),

            _ when RequiredCeiling(requested) <= permitted && !isMinor =>
                new ContentDecision(permitted, Depict: true, Narration.Full),

            // Permitted to happen, not permitted to be shown: cut away. For a minor this is
            // the only outcome intimacy ever has; for an adult it is what an NSFW-disabled
            // game gets instead of a refusal.
            _ => new ContentDecision(permitted, Depict: false, Narration.FadeToBlack),
        };
    }

    /// <summary>The ceiling a scene of this intimacy would need in order to be depicted.</summary>
    private static Ceiling RequiredCeiling(Intimacy intimacy) => intimacy switch
    {
        Intimacy.None => Ceiling.PG13,
        Intimacy.Suggestive => Ceiling.Suggestive,
        Intimacy.Explicit => Ceiling.Explicit,
        _ => throw new ArgumentOutOfRangeException(nameof(intimacy), intimacy, "Unhandled intimacy."),
    };

    private static Ceiling Min(Ceiling a, Ceiling b) => a < b ? a : b;
}

/// <summary>
/// Per-game content configuration. Both values are a game's to choose; neither can lift the
/// under-18 clamp, which is computed from the character rather than read from here.
/// </summary>
/// <param name="MinimumCharacterAge">
/// The youngest character this game permits. 16 allows a teen-romance setting; those
/// characters are still clamped to PG13 by <see cref="ContentPolicy.Resolve"/>.
/// </param>
/// <param name="MaxCeiling">
/// The adult content toggle, expressed as a ceiling. PG13 means the game never depicts
/// intimacy at all; Explicit means adult characters may, subject to the pack.
/// </param>
public sealed record GameContentSettings(int MinimumCharacterAge, Ceiling MaxCeiling)
{
    public static GameContentSettings SafeDefault { get; } =
        new(ContentPolicy.AdultAge, Ceiling.PG13);

    public void Validate()
    {
        if (MinimumCharacterAge < ContentPolicy.LowestPermittedMinimumAge)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MinimumCharacterAge),
                MinimumCharacterAge,
                $"A game may not declare a minimum character age below " +
                $"{ContentPolicy.LowestPermittedMinimumAge}.");
        }
    }
}

/// <summary>How intimate a scene is asking to be. Emitted by the story, never by the player.</summary>
public enum Intimacy
{
    None = 0,
    Suggestive = 1,
    Explicit = 2,
}

/// <summary>
/// What one scene may show and how it may be written.
/// </summary>
/// <param name="Ceiling">
/// The effective ceiling after every cap. This is the value that goes into the image request
/// and therefore into the cache key, so art made under a clamp can never be served to a
/// session that is not clamped.
/// </param>
/// <param name="Depict">Whether an image is generated for the intimate beat at all.</param>
/// <param name="Narration">The constraint the LLM is given for the scene text.</param>
public sealed record ContentDecision(Ceiling Ceiling, bool Depict, Narration Narration);

/// <summary>
/// How the scene may be written. The image ceiling alone is not enough: an undepicted scene
/// that is narrated in detail has not been faded to black in any meaningful sense.
/// </summary>
public enum Narration
{
    /// <summary>Written at the effective ceiling.</summary>
    Full = 0,

    /// <summary>
    /// The beat is acknowledged and passed over. No physical detail, and the scene resumes
    /// afterwards. This is the only form intimacy takes for a character under 18.
    /// </summary>
    FadeToBlack = 1,
}
