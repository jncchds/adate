using Game.Core.Style;

namespace Game.Core.Characters;

/// <summary>
/// The appearance attributes a style pack offers choices for, by the key the pack uses.
/// </summary>
public static class AppearanceFeatures
{
    public const string HairColor = "hairColor";
    public const string HairStyle = "hairStyle";
    public const string EyeColor = "eyeColor";
    public const string SkinTone = "skinTone";
    public const string Build = "build";
    public const string Height = "height";

    /// <summary>Every feature a pack must offer choices for.</summary>
    public static readonly IReadOnlyList<string> All = [HairColor, HairStyle, EyeColor, SkinTone, Build, Height];

    /// <summary>
    /// The features an alternative may move. Build and height are excluded because the words
    /// near them are where juvenile-coded vocabulary lives -- HANDOFF 1.9 names "slim" and
    /// "petite" -- and an alternative the player did not ask for must never drift that way.
    /// Skin tone is excluded because changing it is not a slight variation of the person
    /// described.
    /// </summary>
    public static readonly IReadOnlyList<string> Variable = [HairColor, HairStyle, EyeColor];

    public static string Get(CharacterAppearance appearance, string feature)
    {
        ArgumentNullException.ThrowIfNull(appearance);

        return feature switch
        {
            HairColor => appearance.HairColor,
            HairStyle => appearance.HairStyle,
            EyeColor => appearance.EyeColor,
            SkinTone => appearance.SkinTone,
            Build => appearance.Build,
            Height => appearance.Height,
            _ => throw new ArgumentOutOfRangeException(nameof(feature), feature, "Unknown appearance feature."),
        };
    }

    public static CharacterAppearance With(CharacterAppearance appearance, string feature, string value)
    {
        ArgumentNullException.ThrowIfNull(appearance);

        return feature switch
        {
            HairColor => appearance with { HairColor = value },
            HairStyle => appearance with { HairStyle = value },
            EyeColor => appearance with { EyeColor = value },
            SkinTone => appearance with { SkinTone = value },
            Build => appearance with { Build = value },
            Height => appearance with { Height = value },
            _ => throw new ArgumentOutOfRangeException(nameof(feature), feature, "Unknown appearance feature."),
        };
    }
}

/// <summary>One feature an alternative moved away from what was declared.</summary>
public sealed record FeatureChange(string Feature, string From, string To);

/// <param name="Changes">Empty for the appearance exactly as declared.</param>
public sealed record AppearanceVariant(CharacterAppearance Appearance, IReadOnlyList<FeatureChange> Changes)
{
    public bool IsAsDeclared => Changes.Count == 0;
}

/// <summary>
/// Slight variations of a declared appearance, for the player to choose between.
/// </summary>
/// <remarks>
/// <para>
/// Spike 0 measured the two obvious designs and both miss. Several seeds against identical tags
/// gave one character in several poses, not a choice of looks; several tag sets at one seed gave
/// different people. So a variation here moves tags, but only to a neighbour the pack declares,
/// and the caller holds the seed fixed across all of them.
/// </para>
/// <para>
/// Deterministic for a given seed. Candidates are re-requested whenever the player returns to
/// the page, and a different set each time would both confuse the choice and miss the image cache.
/// </para>
/// </remarks>
public static class AppearanceVariations
{
    /// <summary>
    /// Up to <paramref name="count"/> distinct appearances. The first is always
    /// <paramref name="declared"/> itself. The rest each move one variable feature to a
    /// neighbour, spread across features before any feature is moved twice, and move two
    /// features together only once single moves run out. Fewer are returned when the
    /// vocabulary cannot supply enough, including just the declared appearance when none of
    /// its values have neighbours.
    /// </summary>
    public static IReadOnlyList<AppearanceVariant> For(
        CharacterAppearance declared,
        SubjectProfile profile,
        int count,
        long seed)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        var results = new List<AppearanceVariant>(count) { new(declared, []) };
        var seen = new HashSet<CharacterAppearance> { declared };

        var singles = SpreadAcrossFeatures(SingleChanges(declared, profile)
            .OrderBy(change => Mix(seed, change.Feature, change.To)));

        foreach (var change in singles)
        {
            if (results.Count >= count)
            {
                return results;
            }

            TryAdd([change]);
        }

        for (var i = 0; i < singles.Count && results.Count < count; i++)
        {
            for (var j = i + 1; j < singles.Count && results.Count < count; j++)
            {
                if (!string.Equals(singles[i].Feature, singles[j].Feature, StringComparison.Ordinal))
                {
                    TryAdd([singles[i], singles[j]]);
                }
            }
        }

        return results;

        void TryAdd(IReadOnlyList<FeatureChange> changes)
        {
            var appearance = declared;
            foreach (var change in changes)
            {
                appearance = AppearanceFeatures.With(appearance, change.Feature, change.To);
            }

            if (seen.Add(appearance))
            {
                results.Add(new AppearanceVariant(appearance, changes));
            }
        }
    }

    private static IEnumerable<FeatureChange> SingleChanges(CharacterAppearance declared, SubjectProfile profile)
    {
        foreach (var feature in AppearanceFeatures.Variable)
        {
            var current = AppearanceFeatures.Get(declared, feature);

            // A value the pack does not list -- a save written before choices existed, say --
            // has no declared neighbours, and inventing one would be guessing at the player.
            var option = profile.OptionsFor(feature)
                .FirstOrDefault(o => string.Equals(o.Tag, current, StringComparison.OrdinalIgnoreCase));

            if (option?.Near is null)
            {
                continue;
            }

            foreach (var near in option.Near)
            {
                if (!string.Equals(near, current, StringComparison.OrdinalIgnoreCase))
                {
                    yield return new FeatureChange(feature, current, near);
                }
            }
        }
    }

    /// <summary>
    /// Round-robin across features, keeping each feature's shuffled order. Without this, a
    /// feature with many neighbours -- hair colour usually -- would take every slot and the
    /// player would be offered four hair colours instead of four looks.
    /// </summary>
    private static List<FeatureChange> SpreadAcrossFeatures(IEnumerable<FeatureChange> shuffled)
    {
        var queues = shuffled
            .GroupBy(change => change.Feature, StringComparer.Ordinal)
            .Select(group => new Queue<FeatureChange>(group))
            .ToList();

        var spread = new List<FeatureChange>();
        while (queues.Exists(queue => queue.Count > 0))
        {
            foreach (var queue in queues)
            {
                if (queue.TryDequeue(out var change))
                {
                    spread.Add(change);
                }
            }
        }

        return spread;
    }

    /// <summary>
    /// FNV-1a over the seed and the change. <see cref="string.GetHashCode()"/> is randomised per
    /// process, which would reshuffle the alternatives on every restart.
    /// </summary>
    private static ulong Mix(long seed, string feature, string tag)
    {
        const ulong prime = 1099511628211UL;
        var hash = 14695981039346656037UL ^ unchecked((ulong)seed);

        foreach (var ch in feature)
        {
            hash = unchecked((hash ^ ch) * prime);
        }

        hash = unchecked((hash ^ '|') * prime);

        foreach (var ch in tag)
        {
            hash = unchecked((hash ^ ch) * prime);
        }

        return hash;
    }
}
