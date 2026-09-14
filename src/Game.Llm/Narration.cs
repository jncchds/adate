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

    /// <summary>
    /// Text the model stopped writing in the middle of: an open quote, or no closing punctuation.
    /// </summary>
    public static bool Unfinished(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var trimmed = text.TrimEnd();
        return trimmed.Length == 0
               || trimmed.Count(c => c == '"') % 2 == 1
               || trimmed.Count(c => c == '“') != trimmed.Count(c => c == '”')
               || !".!?\"”’…)*".Contains(trimmed[^1]);
    }

    /// <summary>
    /// Places where the narration decides for the player: "you" followed by something the player does,
    /// says, decides, thinks or feels, outside quoted dialogue. What the player sees or hears is
    /// allowed; the scene is theirs to act in.
    /// </summary>
    public static IReadOnlyList<string> PlayerActions(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var narration = Dialogue().Replace(text, " ");
        return
        [
            .. PlayerAction().Matches(narration)
                .Where(m => !Conditional().IsMatch(narration[..m.Index]))
                .Select(m => m.Value.Trim()),
        ];
    }

    // "if you want", "whether you stay": a condition or an offer, not something the player did.
    [GeneratedRegex(@"\b(if|whether|when|unless|until|once|before|case)\s+$", RegexOptions.IgnoreCase)]
    private static partial Regex Conditional();

    // Straight and curly double quotes. Single quotes double as apostrophes, so they are left alone.
    [GeneratedRegex("\"[^\"]*\"|“[^”]*”")]
    private static partial Regex Dialogue();

    [GeneratedRegex(@"\b(I|I'm|I've|I'd|I'll|me|my|mine|myself|we|we're|we've|us|our|ours|ourselves)\b|\b(Me|My|We|Us|Our)\b")]
    private static partial Regex FirstPerson();

    // "you" as the subject of an action, speech, decision, thought or feeling, allowing a softening
    // adverb in between ("you finally sit"). Perception (see, hear, notice) is deliberately absent.
    [GeneratedRegex(
        @"\byou(?:'re|'ve|'d|'ll)?\s+(?:just\s+|finally\s+|quickly\s+|slowly\s+|both\s+|also\s+|then\s+|carefully\s+|quietly\s+)?" +
        @"(?:walk(?:ed)?|scan(?:ned)?|hesitate[ds]?|sit|sat|stand|stood|take|took|order(?:ed)?|say|said|reply|replied|ask(?:ed)?|answer(?:ed)?|" +
        @"decide[ds]?|choose|chose|smile[ds]?|nod(?:ded)?|laugh(?:ed)?|grin(?:ned)?|shrug(?:ged)?|lean(?:ed)?|reach(?:ed)?|pull(?:ed)?|grab(?:bed)?|" +
        @"pick(?:ed)?|feel|felt|think|thought|wonder(?:ed)?|reali[sz]e[ds]?|remember(?:ed)?|want(?:ed)?|hope[ds]?|agree[ds]?|sip(?:ped)?|wave[ds]?|" +
        @"step(?:ped)?|head(?:ed)?|turn(?:ed)?|glance[ds]?|offer(?:ed)?|tell|told|promise[ds]?|blush(?:ed)?|sigh(?:ed)?|find|found|make|made|" +
        @"settle[ds]?|slide|slid|approach(?:ed)?|move[ds]?|hand(?:ed)?|hold|held|keep|kept|joke[ds]?|tease[ds]?|admit(?:ted)?|apologi[sz]e[ds]?|" +
        @"thank(?:ed)?|greet(?:ed)?|introduce[ds]?|follow(?:ed)?|join(?:ed)?|return(?:ed)?|spend|spent|enjoy(?:ed)?|relax(?:ed)?|hurr(?:y|ied)|" +
        @"run|ran|dance[ds]?|kiss(?:ed)?|hug(?:ged)?|touch(?:ed)?|squeeze[ds]?|pause[ds]?|linger(?:ed)?|catch|caught\s+(?:your|their|his|her)\s+breath)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex PlayerAction();
}
