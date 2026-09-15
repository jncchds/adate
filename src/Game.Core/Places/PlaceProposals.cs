using System.Text.RegularExpressions;
using Game.Core.Content;
using Game.Core.Saves;

namespace Game.Core.Places;

/// <summary>A place the story names during play (plan §10): a type, a name and detail ids.</summary>
/// <param name="Owner">The id of the person present whose home it is, when it is someone's home.</param>
public sealed record PlaceProposal(string Type, string Name, IReadOnlyList<string> Details, string? Owner = null);

/// <summary>
/// Checks a proposed place against the place-type catalog and turns it into a place the player now
/// knows. Only the type and detail ids ever reach an image prompt; the name is display text.
/// </summary>
public static partial class PlaceProposals
{
    public const int MaxNameLength = 40;

    public const int MaxDetails = 3;

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

        return reasons;
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
            FirstDay: day);
    }

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

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotSlug();
}
