using System.Text.RegularExpressions;

namespace Game.Core.Story;

/// <summary>
/// A conversation by text as the bubbles on a phone (user feedback: texting shown as a scene, with the person
/// standing in the player's stairwell, felt wrong). The writer is asked for messages only, each in quotes on its
/// own line; narration it adds anyway is left out whenever there are quoted messages to show.
/// </summary>
public static partial class TextMessages
{
    [GeneratedRegex("\"([^\"]+)\"|“([^”]+)”|«([^»]+)»|„([^“”]+)[“”]")]
    private static partial Regex Quoted();

    /// <summary>The messages in written text, in order; empty for no text.</summary>
    public static IReadOnlyList<string> Split(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!Quoted().IsMatch(text))
        {
            return lines;
        }

        return
        [
            .. lines.SelectMany(line => Quoted().Matches(line)
                .Select(m => m.Groups.Values.Skip(1).First(g => g.Success).Value.Trim())
                .Where(message => message.Length > 0)),
        ];
    }

    /// <summary>The player's own message, without the quotes a proposed reply is written in.</summary>
    public static string Unquote(string reply)
    {
        ArgumentNullException.ThrowIfNull(reply);

        var trimmed = reply.Trim();
        var match = Quoted().Match(trimmed);
        return match.Success && match.Index == 0 && match.Length == trimmed.Length
            ? match.Groups.Values.Skip(1).First(g => g.Success).Value.Trim()
            : trimmed;
    }
}
