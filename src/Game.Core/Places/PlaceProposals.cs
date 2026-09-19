using System.Text.RegularExpressions;
using Game.Core.Content;
using Game.Core.Saves;

namespace Game.Core.Places;

/// <summary>A place the story names during play (plan §10): a type, a name, detail ids and how it looks.</summary>
/// <param name="Owner">The id of the person present whose home it is, when it is someone's home.</param>
/// <param name="Look">A few visual phrases the writer gave it, such as "converted church, stained glass"; drawn, never shown.</param>
public sealed record PlaceProposal(string Type, string Name, IReadOnlyList<string> Details, string? Owner = null, string? Look = null);

/// <summary>
/// Checks a proposed place against the place-type catalog and turns it into a place the player now
/// knows. The type, detail ids and look reach an image prompt; the name is display text.
/// </summary>
/// <remarks>
/// The look bends HANDOFF 1.3 on purpose (user request: a place named in the story should look like what was named, not
/// like any place of its type). It is kept to a short run of words, and the content gate filters it phrase by phrase
/// like an outfit, so the writer adds to an authored description rather than writing the prompt.
/// </remarks>
public static partial class PlaceProposals
{
    public const int MaxNameLength = 40;

    public const int MaxDetails = 3;

    public const int MaxLookLength = 80;

    public const string IdPrefix = "story-";

    /// <summary>Why a proposal cannot be stored, written for a retry prompt. Empty when it can.</summary>
    /// <param name="takenNames">Names the player already knows; proposing one again is refused.</param>
    public static IReadOnlyList<string> Check(PlaceProposal proposal, ILocationCatalog catalog, IEnumerable<string> takenNames)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(takenNames);

        var reasons = new List<string>();
        var types = catalog.All();
        var type = types.FirstOrDefault(t => t.Id == proposal.Type);

        if (type is null)
        {
            reasons.Add($"The place type '{proposal.Type}' is not one of {string.Join(", ", types.Select(t => t.Id))}.");
        }

        var name = proposal.Name?.Trim() ?? "";
        if (name.Length is 0 or > MaxNameLength)
        {
            reasons.Add($"A new place needs a name of 1 to {MaxNameLength} characters; '{proposal.Name}' is not.");
        }
        else if (takenNames.Any(taken => SameName(taken, name)))
        {
            reasons.Add($"'{name}' is already a place the player knows; use it instead of proposing it.");
        }

        var details = proposal.Details ?? [];
        if (details.Count > MaxDetails)
        {
            reasons.Add($"A place has at most {MaxDetails} details; '{name}' has {details.Count}.");
        }

        if (details.Distinct(StringComparer.Ordinal).Count() != details.Count)
        {
            reasons.Add($"'{name}' lists a detail twice.");
        }

        if (type is not null)
        {
            var offered = (type.Details ?? []).Select(d => d.Id).ToList();
            foreach (var detail in details.Where(d => !offered.Contains(d, StringComparer.Ordinal)))
            {
                reasons.Add($"'{detail}' is not a detail of {type.Id}. It offers: {(offered.Count == 0 ? "none" : string.Join(", ", offered))}.");
            }
        }

        if (Spaces().Replace(proposal.Look?.Trim() ?? "", " ").Length > MaxLookLength)
        {
            reasons.Add($"The look of '{name}' is longer than {MaxLookLength} characters; give a few short visual phrases.");
        }

        if (!IsDrawable(proposal.Look))
        {
            reasons.Add($"The look of '{name}' must be written in English: it is drawn, not shown to the player.");
        }

        return reasons;
    }

    /// <summary>
    /// A look as it is stored and drawn: spaces collapsed, cut at a comma or a space to fit <see cref="MaxLookLength"/>,
    /// and only words; null when nothing usable is left.
    /// </summary>
    public static string? CleanLook(string? look)
    {
        var text = Spaces().Replace(look?.Trim() ?? "", " ").Trim(' ', ',', '.', ';');
        if (text.Length > MaxLookLength)
        {
            var cut = text[..(MaxLookLength + 1)];
            var at = cut.LastIndexOf(',') is > 0 and var comma ? comma : cut.LastIndexOf(' ');
            text = (at > 0 ? cut[..at] : text[..MaxLookLength]).Trim(' ', ',', '.', ';');
        }

        return text.Length > 0 && LookWords().IsMatch(text) ? text : null;
    }

    /// <summary>A known story place with its own seed, and an id derived from its name that no other place has.</summary>
    public static PlaceRecord ToRecord(SaveId saveId, PlaceProposal proposal, IEnumerable<string> takenIds, int day)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(takenIds);

        var taken = takenIds.ToHashSet(StringComparer.Ordinal);
        var baseId = IdPrefix + Slug(proposal.Name);
        var id = baseId;
        for (var n = 2; taken.Contains(id); n++)
        {
            id = $"{baseId}-{n}";
        }

        return new PlaceRecord(
            saveId,
            id,
            proposal.Type,
            proposal.Name.Trim(),
            [.. proposal.Details ?? []],
            PlaceRecord.SeedFor(saveId, id),
            PlaceOrigin.Story,
            Known: true,
            FirstDay: day,
            Look: CleanLook(proposal.Look));
    }

    /// <summary>
    /// Whether a look can be drawn: it reaches an image model that reads English, so it has to be written in
    /// the Latin alphabet however the story's names are written. A story in another language otherwise gets
    /// places described to the image model in words it cannot read, and the picture ignores them.
    /// </summary>
    public static bool IsDrawable(string? look) =>
        string.IsNullOrWhiteSpace(look) || !NonLatinLetter().IsMatch(look);

    /// <summary>Names that differ only by case, punctuation, spacing or a leading "the" are the same place.</summary>
    public static bool SameName(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return Comparable(a) == Comparable(b);
    }

    private static string Comparable(string name)
    {
        var words = NotSlug().Replace(name.Trim().ToLowerInvariant(), " ").Trim();
        return words.StartsWith("the ", StringComparison.Ordinal) ? words[4..] : words;
    }

    private static string Slug(string name)
    {
        var slug = NotSlug().Replace(name.Trim().ToLowerInvariant(), "-").Trim('-');
        return slug.Length == 0 ? "place" : slug;
    }

    /// <summary>
    /// Anything that is not a letter, a mark or a digit, in any script. Latin-only here would reduce every
    /// name in another alphabet to nothing, which made all of them the same place and all of their ids
    /// the same slug (found with a Russian story, where no two places could be told apart).
    /// </summary>
    [GeneratedRegex(@"[^\p{L}\p{M}\p{N}]+")]
    private static partial Regex NotSlug();

    /// <summary>A letter that is not Latin: the look is English, so any of these means it was written in another script.</summary>
    [GeneratedRegex(@"[\p{L}-[\p{IsBasicLatin}\p{IsLatin-1Supplement}\p{IsLatinExtended-A}]]")]
    private static partial Regex NonLatinLetter();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^[\p{L}\p{M}\p{N}][\p{L}\p{M}\p{N} ,.'&()\-]*$")]
    private static partial Regex LookWords();
}
