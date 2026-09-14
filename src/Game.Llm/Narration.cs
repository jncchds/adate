using System.Text.RegularExpressions;

namespace Game.Llm;

/// <summary>
/// Checks on how a scene is narrated. The game is first person from the player's eyes, written as
/// "you"; a model that drifts into "I" and "we" narrates someone who is not the player.
/// </summary>
public static partial class Narration
{
    /// <summary>How many first-person words outside dialogue are tolerated before a scene is sent back.</summary>
    public const int FirstPersonTolerance = 2;

    /// <summary>Text longer than this must be split into paragraphs.</summary>
    public const int ParagraphAfter = 600;

    /// <summary>First-person words in the narration, with quoted dialogue removed first.</summary>
    public static IReadOnlyList<string> FirstPersonOutsideDialogue(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var narration = Dialogue().Replace(text, " ");
        return [.. FirstPerson().Matches(narration).Select(m => m.Value)];
    }

    public static bool HasParagraphs(string text) => text.Trim().Contains('\n');

    // Straight and curly double quotes. Single quotes double as apostrophes, so they are left alone.
    [GeneratedRegex("\"[^\"]*\"|“[^”]*”")]
    private static partial Regex Dialogue();

    [GeneratedRegex(@"\b(I|I'm|I've|I'd|I'll|me|my|mine|myself|we|we're|we've|us|our|ours|ourselves)\b|\b(Me|My|We|Us|Our)\b")]
    private static partial Regex FirstPerson();
}
