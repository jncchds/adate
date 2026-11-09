using System.Text.RegularExpressions;

namespace Game.Core.Story;

/// <summary>
/// Small happenings the model proposes for a place type, to be used in a later scene there: the authored list
/// is finite, and a player who comes back to the same café every week met the same dozen incidents. The model
/// only suggests; C# decides what is kept. An idea is one plain sentence about the place, with no named
/// person (it could say something about someone that is not so), no number or clock time (it could
/// contradict the day), nobody addressed as "you", and nothing that decides how anyone feels about anyone.
/// What is kept is stored in the save and offered to a scene by <see cref="UseChance"/>; the authored
/// list is what stands when there is none.
/// </summary>
public static partial class HappeningIdeas
{
    public const int MinLength = 15;
    public const int MaxLength = 140;
    public const int PerAnswer = 2;

    /// <summary>Unused ideas kept per place type in a save, so one busy place does not pile up a backlog.</summary>
    public const int PoolPerPlaceType = 8;

    /// <summary>How often a scene that has an idea waiting uses it rather than the authored list.</summary>
    public const double UseChance = 0.6;

    /// <summary>The rule the answer's <c>happenings</c> field is described by, in the prompt.</summary>
    public const string Rule =
        "- happenings: up to two small ordinary things that could be going on at a place like this on another day " +
        "(a delivery, a noise, someone passing, an object out of place), each one plain sentence in English under 120 characters, " +
        "with no names, no numbers, no clock times, no 'you' and nothing about love or how anyone feels; an empty list if none.";

    // Words that would settle something between people or about the player, which only C# may do.
    [GeneratedRegex(@"\b(you|your|yours|yourself|love|loves|loved|kiss|kisses|kissed|date|dating|marry|married|wedding|breakup|boyfriend|girlfriend|lover|crush)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Settles();

    /// <summary>
    /// The proposed happenings worth keeping.
    /// </summary>
    /// <param name="names">Every name in the story (people and the player): none may appear.</param>
    /// <param name="existing">Happenings already in hand, authored or kept, which a new one may not repeat.</param>
    public static IReadOnlyList<string> Accept(IEnumerable<string>? proposed, IEnumerable<string> names, IEnumerable<string> existing)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(existing);

        var named = names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList();
        var seen = existing.Select(Normal).ToHashSet(StringComparer.Ordinal);
        var kept = new List<string>();

        foreach (var raw in proposed ?? [])
        {
            var text = (raw ?? "").Trim();
            if (text.Length is < MinLength or > MaxLength
                || text.Contains('\n', StringComparison.Ordinal)
                || text.EndsWith('?')
                || text.Any(char.IsDigit)
                || Settles().IsMatch(text)
                || named.Any(n => Mentions(text, n))
                || !seen.Add(Normal(text)))
            {
                continue;
            }

            kept.Add(text);
            if (kept.Count == PerAnswer)
            {
                break;
            }
        }

        return kept;
    }

    private static bool Mentions(string text, string name) =>
        Regex.IsMatch(text, $@"(?<!\p{{L}}){Regex.Escape(name)}(?!\p{{L}})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string Normal(string text) =>
        string.Concat(text.Where(char.IsLetter)).ToLowerInvariant();
}
