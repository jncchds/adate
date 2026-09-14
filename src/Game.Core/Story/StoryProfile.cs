using Game.Core.Cast;

namespace Game.Core.Story;

public sealed record WeightedDesire(string Id, int Weight);

/// <summary>What a character wants from a partner (plan §6), beyond the primary want on their cast record.</summary>
/// <param name="Desires">Strongest first.</param>
/// <param name="Aversion">
/// A desire this character counts against: another cast member's top desire, so the choice that
/// raises one of them costs another.
/// </param>
/// <param name="Need">Hidden. Required to reach <c>committed</c>.</param>
/// <param name="LikedPlaceTypes">Place types a date goes well at.</param>
/// <param name="DislikedPlaceTypes">Place types a date goes badly at.</param>
public sealed record StoryProfile(
    IReadOnlyList<WeightedDesire> Desires,
    string? Aversion,
    IReadOnlyList<string> Dealbreakers,
    string Need,
    IReadOnlyList<string> LikedPlaceTypes,
    IReadOnlyList<string> DislikedPlaceTypes)
{
    public int WeightOf(string desireId) =>
        Desires.FirstOrDefault(d => string.Equals(d.Id, desireId, StringComparison.Ordinal))?.Weight ?? 0;
}

/// <summary>
/// Builds a story profile per cast member, deterministically from a seed. Top desires differ across
/// the cast and each member averts the next member's top desire, so a choice never pleases everyone:
/// "who looks right" becomes "who fits".
/// </summary>
public static class StoryProfileGenerator
{
    public static IReadOnlyList<StoryProfile> For(
        IReadOnlyList<CastMember> cast,
        StoryContent content,
        IReadOnlyList<string> placeTypes,
        long seed)
    {
        ArgumentNullException.ThrowIfNull(cast);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(placeTypes);

        if (cast.Count == 0)
        {
            throw new ArgumentException("A cast has at least the main LI.", nameof(cast));
        }

        var desireIds = content.Values.Desires.Select(d => d.Id).ToList();
        if (cast.Count > desireIds.Count)
        {
            throw new ArgumentException($"A cast of {cast.Count} needs {cast.Count} different top desires; content has {desireIds.Count}.", nameof(cast));
        }

        var weights = content.Rules.DesireWeights;
        var shared = new StoryRng(StoryRng.Mix((ulong)seed, 0));
        var tops = shared.Shuffle(desireIds);
        var needs = shared.Shuffle(content.Values.Needs.Select(n => n.Id));
        var places = placeTypes.Distinct(StringComparer.Ordinal).ToList();

        var profiles = new List<StoryProfile>(cast.Count);
        for (var i = 0; i < cast.Count; i++)
        {
            var rng = new StoryRng(StoryRng.Mix((ulong)seed, (ulong)i + 1));

            var top = tops[i];
            var aversion = cast.Count > 1 ? tops[(i + 1) % cast.Count] : null;

            var rest = rng.Shuffle(desireIds.Where(id => id != top && id != aversion));
            var desires = new[] { top }
                .Concat(rest.Take(weights.Count - 1))
                .Select((id, rank) => new WeightedDesire(id, weights[rank]))
                .ToList();

            var dealbreakers = rng.Shuffle(content.Values.Dealbreakers.Select(d => d.Id)).Take(1 + rng.Below(2)).ToList();

            var shuffledPlaces = rng.Shuffle(places);
            var liked = shuffledPlaces.Take(Math.Min(2, shuffledPlaces.Count)).ToList();
            var disliked = shuffledPlaces.Skip(liked.Count).Take(1).ToList();

            profiles.Add(new StoryProfile(desires, aversion, dealbreakers, needs[i % needs.Count], liked, disliked));
        }

        return profiles;
    }
}

/// <summary>SplitMix64: small, fast and the same on every platform, so a seed always builds the same story.</summary>
internal sealed class StoryRng(ulong state)
{
    private ulong _state = state;

    public static ulong Mix(ulong a, ulong b)
    {
        var z = a ^ (b * 0x9E3779B97F4A7C15UL);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public ulong Next()
    {
        _state += 0x9E3779B97F4A7C15UL;
        return Mix(_state, 0);
    }

    public int Below(int n) => (int)(Next() % (ulong)n);

    public List<T> Shuffle<T>(IEnumerable<T> items)
    {
        var list = items.ToList();
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = Below(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }

        return list;
    }
}
