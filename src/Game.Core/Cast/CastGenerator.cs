using System.Globalization;
using Game.Core.Characters;
using Game.Core.Style;

namespace Game.Core.Cast;

/// <summary>What a contrast profile may move. The appearance features plus aesthetic, feature and age.</summary>
public static class LookDimensions
{
    public const string HairColor = AppearanceFeatures.HairColor;
    public const string HairStyle = AppearanceFeatures.HairStyle;
    public const string EyeColor = AppearanceFeatures.EyeColor;
    public const string SkinTone = AppearanceFeatures.SkinTone;
    public const string Build = AppearanceFeatures.Build;
    public const string Height = AppearanceFeatures.Height;
    public const string Aesthetic = "aesthetic";
    public const string Feature = "feature";
    public const string Age = "age";

    public static readonly IReadOnlyList<string> All = [HairColor, HairStyle, EyeColor, SkinTone, Build, Height, Aesthetic, Feature, Age];

    /// <summary>
    /// Changes that read at a glance. Build is deliberately not one: measured on Z-Image, no build
    /// wording moved the render at all, so a variant whose only big change was its build would look
    /// like the main LI.
    /// </summary>
    public static readonly IReadOnlyList<string> Silhouette = [HairStyle, Aesthetic, Age];
}

public sealed record CastChange(string Dimension, string From, string To);

/// <param name="ProfileId">The contrast profile this member was built from; null for the main LI.</param>
/// <param name="Aesthetic">The style aesthetic that dresses them, an id from the subject's aesthetics.</param>
/// <param name="Temper">Axis id to end id, for every axis.</param>
/// <param name="Seed">The anchor seed. Each member has their own: a shared seed is what makes candidates one person.</param>
/// <param name="LookChanges">How the look differs from the main LI's. Empty for the main LI.</param>
public sealed record CastMember(
    string? ProfileId,
    CharacterAppearance Appearance,
    string Aesthetic,
    IReadOnlyDictionary<string, string> Temper,
    string WantId,
    long Seed,
    IReadOnlyList<CastChange> LookChanges)
{
    public bool IsMain => ProfileId is null;

    public IReadOnlyList<string> FlippedAxes(CastMember main)
    {
        ArgumentNullException.ThrowIfNull(main);
        return [.. Temper.Where(kv => !string.Equals(main.Temper[kv.Key], kv.Value, StringComparison.Ordinal)).Select(kv => kv.Key)];
    }

    /// <summary>The expression most of their temper ends rest on; ties go to the earlier axis.</summary>
    public string RestingExpression(CastContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var votes = content.Temper.Select(axis => axis.End(Temper[axis.Id]).Resting).ToList();

        return votes
            .GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => votes.FindIndex(v => string.Equals(v, g.Key, StringComparison.OrdinalIgnoreCase)))
            .First()
            .Key;
    }
}

/// <summary>
/// Builds the alternatives to a main LI from contrast profiles (phase-2 plan §5).
/// </summary>
/// <remarks>
/// <para>
/// Deterministic for a seed: the cast is fixed when a game starts and must come out the same every
/// time it is rebuilt, or the image cache misses and the player meets different people.
/// </para>
/// <para>
/// What never varies: subject; an adult main LI's variants stay adult, within ten years above them;
/// a main LI under 18 passes their exact age to every variant; and choices marked
/// <see cref="FeatureOption.PlayerOnly"/> are never generated. No two members share a hair style,
/// an aesthetic or an age that a profile moved, so the comparison always has something to show.
/// </para>
/// </remarks>
public static class CastGenerator
{
    private static readonly IReadOnlyList<string> Fallbacks =
        [LookDimensions.HairColor, LookDimensions.EyeColor, LookDimensions.HairStyle, LookDimensions.Aesthetic];

    /// <summary>
    /// A main LI with a stored temper for a save created before the new-game flow asked for one.
    /// The temper is deterministic from the anchor seed; want and aesthetic come from <see cref="Main"/>.
    /// </summary>
    public static CastMember PlaceholderMain(
        CharacterAppearance appearance,
        SubjectProfile subject,
        CastContent content,
        long anchorSeed)
    {
        ArgumentNullException.ThrowIfNull(content);

        var rng = new Rng(Hash(anchorSeed, "main"));

        var temper = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var axis in content.Temper)
        {
            temper[axis.Id] = rng.Pick(axis.Ends).Id;
        }

        return Main(appearance, temper, subject, content, anchorSeed);
    }

    /// <summary>
    /// The main LI as the player described them, temper included. Their want and aesthetic are not
    /// asked for: the want is a story fact the player discovers, and the aesthetic follows the temper.
    /// Both are deterministic from the anchor seed.
    /// </summary>
    public static CastMember Main(
        CharacterAppearance appearance,
        IReadOnlyDictionary<string, string> temper,
        SubjectProfile subject,
        CastContent content,
        long anchorSeed)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        ArgumentNullException.ThrowIfNull(temper);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(content);

        foreach (var axis in content.Temper)
        {
            if (!temper.TryGetValue(axis.Id, out var end) || axis.Ends.All(e => e.Id != end))
            {
                throw new ArgumentException($"The temper has no valid end for axis '{axis.Id}'.", nameof(temper));
            }
        }

        var rng = new Rng(Hash(anchorSeed, "main-want"));

        var want = rng.Pick(content.Wants).Id;

        var aesthetics = subject.Aesthetics?.Keys.Order(StringComparer.Ordinal).ToList() ?? [];
        var aesthetic = aesthetics.Count == 0 ? "" : Leaning(temper, content, aesthetics, rng);

        return new CastMember(
            null, appearance, aesthetic, new Dictionary<string, string>(temper, StringComparer.Ordinal), want, anchorSeed, []);
    }

    /// <summary>One member per contrast profile, in content order.</summary>
    public static IReadOnlyList<CastMember> For(
        CastMember main,
        SubjectProfile subject,
        CastContent content,
        long seed)
    {
        ArgumentNullException.ThrowIfNull(main);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(content);

        if (!main.IsMain)
        {
            throw new ArgumentException("Variants are built from the main LI, not from another variant.", nameof(main));
        }

        // Measured on the debug page: two variants both moved to purple hair, and the comparison
        // blurred. Hair colour reads first, so it is kept apart like the silhouette dimensions.
        var taken = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            [LookDimensions.HairColor] = new(StringComparer.OrdinalIgnoreCase) { main.Appearance.HairColor },
            [LookDimensions.HairStyle] = new(StringComparer.OrdinalIgnoreCase) { main.Appearance.HairStyle },
            [LookDimensions.Aesthetic] = new(StringComparer.OrdinalIgnoreCase) { main.Aesthetic },
            [LookDimensions.Age] = new(StringComparer.Ordinal) { AgeKey(main.Appearance.Age) },
        };
        var usedWants = new HashSet<string>(StringComparer.Ordinal) { main.WantId };

        var members = new List<CastMember>(content.Contrasts.Count);

        foreach (var profile in content.Contrasts)
        {
            var rng = new Rng(Hash(seed, profile.Id));

            var temper = FlipTemper(main.Temper, profile.TemperFlips, content, rng);

            var want = PickWant(main.WantId, profile.Want, content, usedWants, rng);
            usedWants.Add(want);

            var look = new LookBuilder(main, subject, content, temper, taken, rng);
            foreach (var dimension in profile.Look)
            {
                look.TryChange(dimension);
            }

            // A profile names what it would like to move; when the vocabulary cannot supply one of
            // those (an under-18 main LI keeps their age, say), the budget still has to be met.
            foreach (var fallback in Fallbacks)
            {
                if (look.MeetsBudget)
                {
                    break;
                }

                if (!look.Changed(fallback))
                {
                    look.TryChange(fallback);
                }
            }

            if (!look.MeetsBudget)
            {
                throw new InvalidOperationException(
                    $"Contrast profile '{profile.Id}' could not differ from the main LI in {CastContent.MinimumLookChanges} " +
                    "look dimensions including one visible in silhouette with this subject's vocabulary.");
            }

            members.Add(new CastMember(
                profile.Id,
                look.Appearance,
                look.Aesthetic,
                temper,
                want,
                SeedFrom(Hash(seed, "seed:" + profile.Id)),
                look.Changes));
        }

        return members;
    }

    private static Dictionary<string, string> FlipTemper(
        IReadOnlyDictionary<string, string> main,
        TemperFlips flips,
        CastContent content,
        Rng rng)
    {
        var temper = new Dictionary<string, string>(main, StringComparer.Ordinal);

        var axes = flips.Axes ?? Shuffle(content.Temper.Select(a => a.Id).ToList(), rng).Take(flips.Count ?? 0).ToList();

        foreach (var id in axes)
        {
            temper[id] = content.Axis(id).Other(temper[id]).Id;
        }

        return temper;
    }

    private static string PickWant(
        string mainWantId,
        WantRelation relation,
        CastContent content,
        HashSet<string> used,
        Rng rng)
    {
        var main = content.Want(mainWantId);
        var candidates = content.Wants.Where(w => !used.Contains(w.Id)).ToList();

        List<WantDefinition> Pool(WantRelation r) => r switch
        {
            WantRelation.SameFamily => [.. candidates.Where(w => w.Family == main.Family && !main.ConflictsWith.Contains(w.Id))],
            WantRelation.Conflicting => [.. candidates.Where(w => main.ConflictsWith.Contains(w.Id))],
            _ => [.. candidates.Where(w => w.Family != main.Family && !main.ConflictsWith.Contains(w.Id))],
        };

        var pool = Pool(relation);
        if (pool.Count == 0 && relation is not WantRelation.DifferentFamily)
        {
            pool = Pool(WantRelation.DifferentFamily);
        }

        if (pool.Count == 0)
        {
            pool = candidates;
        }

        return pool.Count == 0
            ? throw new InvalidOperationException("Cast content has too few wants to give every member a different one.")
            : rng.Pick(pool).Id;
    }

    /// <summary>The candidate most of the temper's ends lean towards; ties broken by seed.</summary>
    private static string Leaning(
        IReadOnlyDictionary<string, string> temper,
        CastContent content,
        IReadOnlyList<string> candidates,
        Rng rng)
    {
        var votes = candidates.ToDictionary(c => c, _ => 0, StringComparer.OrdinalIgnoreCase);

        foreach (var axis in content.Temper)
        {
            foreach (var leaning in axis.End(temper[axis.Id]).Aesthetics)
            {
                if (votes.ContainsKey(leaning))
                {
                    votes[leaning]++;
                }
            }
        }

        var best = votes.Values.Max();
        return rng.Pick(candidates.Where(c => votes[c] == best).ToList());
    }

    private sealed class LookBuilder(
        CastMember main,
        SubjectProfile subject,
        CastContent content,
        IReadOnlyDictionary<string, string> temper,
        Dictionary<string, HashSet<string>> taken,
        Rng rng)
    {
        private readonly List<CastChange> _changes = [];

        public CharacterAppearance Appearance { get; private set; } = main.Appearance;

        public string Aesthetic { get; private set; } = main.Aesthetic;

        public IReadOnlyList<CastChange> Changes => _changes;

        public bool MeetsBudget =>
            _changes.Count >= CastContent.MinimumLookChanges &&
            _changes.Exists(c => LookDimensions.Silhouette.Contains(c.Dimension, StringComparer.Ordinal));

        public bool Changed(string dimension) => _changes.Exists(c => c.Dimension == dimension);

        public bool TryChange(string dimension) => dimension switch
        {
            LookDimensions.Aesthetic => ChangeAesthetic(),
            LookDimensions.Feature => ChangeFeature(),
            LookDimensions.Age => ChangeAge(),
            _ => ChangeOption(dimension),
        };

        private bool ChangeOption(string dimension)
        {
            var current = AppearanceFeatures.Get(Appearance, dimension);
            var all = subject.OptionsFor(dimension);

            var pool = all
                .Where(o => !o.PlayerOnly && !Same(o.Tag, current) && !IsTaken(dimension, o.Tag))
                .ToList();

            if (pool.Count == 0)
            {
                return false;
            }

            // A neighbour is what a candidate portrait offers. An alternative is meant to be a
            // different person, so neighbours are only used when nothing further away exists.
            var currentOption = all.FirstOrDefault(o => Same(o.Tag, current));
            var far = pool.Where(o => currentOption?.Near?.Contains(o.Tag, StringComparer.OrdinalIgnoreCase) is not true).ToList();
            if (far.Count > 0)
            {
                pool = far;
            }

            FeatureOption chosen;
            if (dimension == LookDimensions.SkinTone && currentOption is not null)
            {
                // Skin tone choices are listed light to dark; a profile that moves it moves it far.
                var at = IndexOf(all, current);
                var distance = pool.Max(o => Math.Abs(IndexOf(all, o.Tag) - at));
                chosen = rng.Pick(pool.Where(o => Math.Abs(IndexOf(all, o.Tag) - at) == distance).ToList());
            }
            else
            {
                chosen = rng.Pick(pool);
            }

            Record(dimension, current, chosen.Tag);
            Appearance = AppearanceFeatures.With(Appearance, dimension, chosen.Tag);
            return true;
        }

        private bool ChangeAesthetic()
        {
            var pool = (subject.Aesthetics?.Keys ?? [])
                .Order(StringComparer.Ordinal)
                .Where(k => !Same(k, Aesthetic) && !IsTaken(LookDimensions.Aesthetic, k))
                .ToList();

            if (pool.Count == 0)
            {
                return false;
            }

            var chosen = Leaning(temper, content, pool, rng);
            Record(LookDimensions.Aesthetic, Aesthetic, chosen);
            Aesthetic = chosen;
            return true;
        }

        private bool ChangeFeature()
        {
            var current = Appearance.DistinguishingFeature ?? "";
            var pool = (subject.CastFeatures ?? []).Where(f => !Same(f, current)).ToList();

            if (pool.Count == 0)
            {
                return false;
            }

            var chosen = rng.Pick(pool);
            Record(LookDimensions.Feature, current, chosen);
            Appearance = Appearance with { DistinguishingFeature = chosen };
            return true;
        }

        private bool ChangeAge()
        {
            var age = main.Appearance.Age;

            // A main LI under 18 passes their exact age on; nothing here generates a younger or a
            // differently-aged minor.
            if (age < 18)
            {
                return false;
            }

            var band = subject.BandFor(age).From;
            var candidates = Enumerable.Range(18, age + 10 - 18 + 1)
                .Where(a => subject.BandFor(a).From != band && !IsTaken(LookDimensions.Age, AgeKey(a)))
                .ToList();

            if (candidates.Count == 0)
            {
                return false;
            }

            // Crossing a band boundary by a year changes nothing a player can see: 24 against 25
            // read as the same person in the distinctness check. Ask for a real gap, older where
            // the cap allows, so "a different chapter" looks like one; failing that, the widest gap.
            var apart = candidates.Where(a => Math.Abs(a - age) >= MinimumAgeGap).ToList();
            var older = apart.Where(a => a > age).ToList();
            var pool = older.Count > 0 ? older
                : apart.Count > 0 ? apart
                : candidates.Where(a => Math.Abs(a - age) == candidates.Max(c => Math.Abs(c - age))).ToList();

            var chosen = rng.Pick(pool);
            Record(LookDimensions.Age, AgeKey(age), AgeKey(chosen));
            Appearance = Appearance with { Age = chosen };
            return true;
        }

        private void Record(string dimension, string from, string to)
        {
            _changes.Add(new CastChange(dimension, from, to));

            if (taken.TryGetValue(dimension, out var values))
            {
                values.Add(to);
            }
        }

        private bool IsTaken(string dimension, string value) =>
            taken.TryGetValue(dimension, out var values) && values.Contains(value);

        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static int IndexOf(IReadOnlyList<FeatureOption> options, string tag)
        {
            for (var i = 0; i < options.Count; i++)
            {
                if (Same(options[i].Tag, tag))
                {
                    return i;
                }
            }

            return -1;
        }
    }

    /// <summary>The fewest years an age change moves, when the adult range and the ten-year cap allow it.</summary>
    public const int MinimumAgeGap = 5;

    private static string AgeKey(int age) => age.ToString(CultureInfo.InvariantCulture);

    private static List<T> Shuffle<T>(List<T> items, Rng rng)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = (int)(rng.Next() % (ulong)(i + 1));
            (items[i], items[j]) = (items[j], items[i]);
        }

        return items;
    }

    /// <summary>A non-negative seed that fits every provider's seed input.</summary>
    private static long SeedFrom(ulong hash) => (long)(hash % int.MaxValue);

    /// <summary>FNV-1a over a seed and a label. <see cref="string.GetHashCode()"/> is randomised per process.</summary>
    private static ulong Hash(long seed, string label)
    {
        const ulong prime = 1099511628211UL;
        var hash = 14695981039346656037UL ^ unchecked((ulong)seed);

        foreach (var ch in label)
        {
            hash = unchecked((hash ^ ch) * prime);
        }

        return hash;
    }

    /// <summary>SplitMix64. Stable across runtimes, unlike <see cref="Random"/>'s unspecified algorithm.</summary>
    private sealed class Rng(ulong seed)
    {
        private ulong _state = seed;

        public ulong Next()
        {
            var z = _state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        public T Pick<T>(IReadOnlyList<T> items) => items[(int)(Next() % (ulong)items.Count)];
    }
}
