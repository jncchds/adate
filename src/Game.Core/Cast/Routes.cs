using System.Text.Json;
using System.Text.RegularExpressions;
using Game.Core.Story;

namespace Game.Core.Cast;

/// <summary>How a variant is met (plan §5).</summary>
/// <param name="Suits">Temper ends that fit this route. The assigner maximises how many each variant holds.</param>
public sealed record RouteDefinition(string Id, string Label, IReadOnlyList<string> Suits);

/// <summary>
/// The routes variants are met by, and placeholder names by subject until the story bible names
/// people (plan §7). Content, so a route or a name is data rather than a release.
/// </summary>
public sealed partial record RouteContent(IReadOnlyList<RouteDefinition> Routes, IReadOnlyDictionary<string, IReadOnlyList<string>> Names)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static RouteContent Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Route content '{path}' not found.", path);
        }

        var content = JsonSerializer.Deserialize<RouteContent>(File.ReadAllText(path), Json)
            ?? throw new InvalidOperationException($"Route content '{path}' deserialised to null.");

        content.Validate();
        return content;
    }

    public void Validate()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var route in Routes)
        {
            if (!RouteId().IsMatch(route.Id) || !ids.Add(route.Id))
            {
                throw new InvalidOperationException($"Route id '{route.Id}' is blank, duplicated, or not lower-case letters, digits and dashes.");
            }

            if (route.Suits.Count == 0)
            {
                throw new InvalidOperationException($"Route '{route.Id}' suits no temper end, so no variant would ever fit it better than another.");
            }
        }

        foreach (var (subject, names) in Names)
        {
            if (names.Count < Routes.Count + 1 || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
            {
                throw new InvalidOperationException(
                    $"Subject '{subject}' needs at least {Routes.Count + 1} different names: one per variant, with room to avoid the main LI's.");
            }
        }
    }

    /// <summary>Rules that depend on the cast vocabulary: one route per contrast profile, suiting real temper ends.</summary>
    public void ValidateAgainst(CastContent cast)
    {
        ArgumentNullException.ThrowIfNull(cast);

        if (Routes.Count != cast.Contrasts.Count)
        {
            throw new InvalidOperationException(
                $"Route content declares {Routes.Count} routes for {cast.Contrasts.Count} contrast profiles; each variant takes one route.");
        }

        var ends = cast.Temper.SelectMany(a => a.Ends).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var route in Routes)
        {
            foreach (var end in route.Suits)
            {
                if (!ends.Contains(end))
                {
                    throw new InvalidOperationException($"Route '{route.Id}' suits '{end}', which is not a temper end.");
                }
            }
        }
    }

    /// <summary>
    /// Placeholder names for a subject, none of them already taken (case-insensitive), deterministic
    /// from the seed. A subject with no list of its own draws from every list.
    /// </summary>
    public IReadOnlyList<string> PickNames(string subject, int count, IEnumerable<string> taken, long seed)
    {
        ArgumentNullException.ThrowIfNull(taken);

        var pool = Names.TryGetValue(subject, out var own) ? own : [.. Names.Values.SelectMany(n => n)];
        var takenSet = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var free = new StoryRng(StoryRng.Mix((ulong)seed, 0x4E414D45UL)).Shuffle(pool.Where(n => !takenSet.Contains(n)));

        return free.Count >= count
            ? free.Take(count).ToList()
            : throw new InvalidOperationException($"Subject '{subject}' has {free.Count} free names; {count} are needed.");
    }

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex RouteId();
}

/// <summary>
/// Gives each variant a route by temper (plan §5: temper picks the route): the assignment where the
/// most suited temper ends line up wins, and ties go to the earliest in cast and content order.
/// </summary>
public static class RouteAssigner
{
    public static IReadOnlyList<string> Assign(IReadOnlyList<CastMember> variants, RouteContent content)
    {
        ArgumentNullException.ThrowIfNull(variants);
        ArgumentNullException.ThrowIfNull(content);

        if (variants.Count != content.Routes.Count)
        {
            throw new ArgumentException($"{variants.Count} variants cannot take {content.Routes.Count} routes one each.", nameof(variants));
        }

        int[]? best = null;
        var bestScore = -1;

        foreach (var order in Permutations([.. Enumerable.Range(0, variants.Count)]))
        {
            var score = order.Select((route, i) => Fit(variants[i], content.Routes[route])).Sum();
            if (score > bestScore)
            {
                best = order;
                bestScore = score;
            }
        }

        return [.. best!.Select(route => content.Routes[route].Id)];
    }

    /// <summary>How many of the route's suited temper ends the member holds.</summary>
    public static int Fit(CastMember member, RouteDefinition route)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(route);

        return route.Suits.Count(end => member.Temper.Values.Contains(end, StringComparer.Ordinal));
    }

    /// <summary>Every ordering, in lexicographic order, so the first best one is stable.</summary>
    private static IEnumerable<int[]> Permutations(int[] items)
    {
        if (items.Length <= 1)
        {
            yield return items;
            yield break;
        }

        for (var i = 0; i < items.Length; i++)
        {
            var rest = items.Where((_, j) => j != i).ToArray();
            foreach (var tail in Permutations(rest))
            {
                yield return [items[i], .. tail];
            }
        }
    }
}
