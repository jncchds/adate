namespace Game.Core.Story;

/// <summary>How other people refer to the player, from the gender chosen on the new-game form.</summary>
public static class PlayerPronouns
{
    /// <returns>The pronouns, or null for a save without a gender, which leaves the writer to avoid them.</returns>
    public static string? For(string? gender) => gender?.Trim().ToLowerInvariant() switch
    {
        "woman" => "she/her",
        "man" => "he/him",
        "nonbinary" => "they/them",
        _ => null,
    };
}
