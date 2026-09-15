using System.Text.RegularExpressions;

namespace Game.Core.Story;

/// <summary>
/// The language a save's story is written in. The player types any language when starting a game; the
/// model writes the prose in it, while ids, tags and everything C# reads stay English. Only English
/// stories get the English-specific narration checks (first person, player actions).
/// </summary>
public static partial class NarrationLanguage
{
    public const string Default = "English";

    public const int MaxLength = 40;

    /// <summary>
    /// The typed language, trimmed and with spaces collapsed; English when left blank. Only letters,
    /// spaces, hyphens, apostrophes and parentheses are accepted, because the name goes into every prompt.
    /// </summary>
    public static string Normalize(string? typed)
    {
        var collapsed = Spaces().Replace(typed?.Trim() ?? "", " ");
        if (collapsed.Length == 0)
        {
            return Default;
        }

        return collapsed.Length <= MaxLength && Name().IsMatch(collapsed)
            ? collapsed
            : throw new ArgumentException($"Enter a language by name, such as English or Русский, up to {MaxLength} letters.");
    }

    /// <summary>Whether the story is English: no language stored (saves from before languages), or English by name.</summary>
    public static bool IsEnglish(string? language) =>
        string.IsNullOrWhiteSpace(language)
        || language.Trim().StartsWith(Default, StringComparison.OrdinalIgnoreCase)
        || language.Trim() is "en" or "EN" or "eng";

    /// <summary>
    /// The extra writing rules for a story in <paramref name="language"/>; none for English. Shared by the
    /// scene packet and the epilogue, so every prompt asks for the same thing.
    /// </summary>
    /// <param name="playerGender">woman, man or nonbinary: where the language marks gender, forms for the player follow it.</param>
    public static IReadOnlyList<string> WritingRules(string? language, string? playerGender)
    {
        if (IsEnglish(language))
        {
            return [];
        }

        var rules = new List<string>
        {
            $"Write the prose in {language}: the text, and any summary and choices.",
            "Keep everything else exactly as listed, in English: JSON keys, ids, tags, expressions, predicates, place types and place details.",
            "Write people's names as they are given above.",
        };

        var agreement = playerGender?.Trim().ToLowerInvariant() switch
        {
            "woman" => $"The player is a woman: wherever {language} marks grammatical gender (verbs, adjectives, forms of address), use feminine forms for the player.",
            "man" => $"The player is a man: wherever {language} marks grammatical gender (verbs, adjectives, forms of address), use masculine forms for the player.",
            "nonbinary" => $"The player is nonbinary: wherever {language} marks grammatical gender, use gender-neutral forms for the player where the language has them, otherwise plural forms.",
            _ => null,
        };

        if (agreement is not null)
        {
            rules.Add(agreement);
        }

        return rules;
    }

    /// <summary>
    /// The language rules for reading data out of prose that is already written (two-pass writing); none for English.
    /// Without them the summary and choices came back in English under a Russian scene.
    /// </summary>
    public static IReadOnlyList<string> DataRules(string? language, string? playerGender)
    {
        if (IsEnglish(language))
        {
            return [];
        }

        return
        [
            $"Write the summary and choices in {language}, the language of the scene.",
            .. WritingRules(language, playerGender).Skip(1),
        ];
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^[\p{L}\p{M}][\p{L}\p{M} '\-()]*$")]
    private static partial Regex Name();
}
