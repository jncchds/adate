using System.Text.Json;
using System.Text.Json.Serialization;
using Game.Core.Style;

namespace Game.Core.Cast;

/// <param name="Resting">The expression slot this end rests on, one of the pack's <c>expressions</c>.</param>
/// <param name="Aesthetics">Style aesthetics this end leans towards, ids from the subject's <c>aesthetics</c>.</param>
public sealed record TemperEnd(string Id, string Label, string Writing, string Resting, IReadOnlyList<string> Aesthetics);

/// <summary>One temper axis. Always exactly two ends, so "the other end" is always defined.</summary>
/// <param name="Dresses">
/// Whether this axis picks the aesthetic: only its end's leanings are candidates, and the rest of the temper votes among
/// them (user feedback: the everyday outfit on the map should follow the temper but show the LI's energy). At most one axis.
/// </param>
public sealed record TemperAxis(string Id, string Label, IReadOnlyList<TemperEnd> Ends, bool Dresses = false)
{
    public TemperEnd End(string endId) =>
        Ends.FirstOrDefault(e => string.Equals(e.Id, endId, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"Temper axis '{Id}' has no end '{endId}'.");

    public TemperEnd Other(string endId) =>
        string.Equals(Ends[0].Id, endId, StringComparison.Ordinal) ? Ends[1] : Ends[0];
}

/// <param name="Family">Wants in one family are compatible ambitions. A variant that shares the main LI's heart picks from it.</param>
/// <param name="ConflictsWith">Wants that cannot both be won. Symmetric; the loader refuses a one-sided conflict.</param>
public sealed record WantDefinition(string Id, string Label, string Family, IReadOnlyList<string> ConflictsWith);

public enum WantRelation
{
    SameFamily,
    Conflicting,
    DifferentFamily,
}

public enum AgeRule
{
    Same,
    DifferentBand,
}

/// <summary>Either named axes to flip, or how many to flip chosen by seed. Never both.</summary>
public sealed record TemperFlips(IReadOnlyList<string>? Axes = null, int? Count = null)
{
    public int Total => Axes?.Count ?? Count ?? 0;
}

/// <summary>
/// How one alternative is built from the main LI (phase-2 plan §5): which look dimensions move,
/// how many temper axes flip, how its want relates, and whether its age band changes.
/// </summary>
public sealed record ContrastProfile(
    string Id,
    string Question,
    IReadOnlyList<string> Look,
    TemperFlips TemperFlips,
    WantRelation Want,
    AgeRule Age);

/// <summary>
/// The cast vocabulary: temper axes, wants and contrast profiles. Content, like style packs and
/// locations, so a new axis or profile is data rather than a release.
/// </summary>
public sealed record CastContent(
    IReadOnlyList<TemperAxis> Temper,
    IReadOnlyList<WantDefinition> Wants,
    IReadOnlyList<ContrastProfile> Contrasts)
{
    /// <summary>A variant differs from the main LI in at least this many look dimensions (plan §5).</summary>
    public const int MinimumLookChanges = 3;

    /// <summary>And in at least this many temper axes.</summary>
    public const int MinimumTemperFlips = 2;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static CastContent Load(string temperPath, string wantsPath, string contrastsPath)
    {
        var content = new CastContent(
            Read<List<TemperAxis>>(temperPath),
            Read<List<WantDefinition>>(wantsPath),
            Read<List<ContrastProfile>>(contrastsPath));

        content.Validate();
        return content;
    }

    public TemperAxis Axis(string id) =>
        Temper.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"Unknown temper axis '{id}'.");

    public WantDefinition Want(string id) =>
        Wants.FirstOrDefault(w => string.Equals(w.Id, id, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"Unknown want '{id}'.");

    /// <summary>Rules that need nothing but the content itself.</summary>
    public void Validate()
    {
        if (Temper.Count < MinimumTemperFlips)
        {
            throw new InvalidOperationException($"Cast content declares {Temper.Count} temper axes; a variant must flip {MinimumTemperFlips}.");
        }

        var endIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var axis in Temper)
        {
            if (axis.Ends.Count != 2)
            {
                throw new InvalidOperationException(
                    $"Temper axis '{axis.Id}' has {axis.Ends.Count} ends. An axis has exactly two, so flipping it always has one answer.");
            }

            foreach (var end in axis.Ends)
            {
                if (!endIds.Add(end.Id))
                {
                    throw new InvalidOperationException($"Temper end id '{end.Id}' is used twice.");
                }

                if (string.IsNullOrWhiteSpace(end.Resting))
                {
                    throw new InvalidOperationException($"Temper end '{end.Id}' has no resting expression.");
                }
            }
        }

        if (Temper.Count(a => a.Dresses) > 1)
        {
            throw new InvalidOperationException(
                $"Temper axes {string.Join(", ", Temper.Where(a => a.Dresses).Select(a => $"'{a.Id}'"))} all pick the aesthetic; only one may.");
        }

        Unique(Temper.Select(a => a.Id), "temper axis");
        Unique(Wants.Select(w => w.Id), "want");
        Unique(Contrasts.Select(c => c.Id), "contrast profile");

        foreach (var want in Wants)
        {
            foreach (var other in want.ConflictsWith)
            {
                var target = Wants.FirstOrDefault(w => w.Id == other)
                    ?? throw new InvalidOperationException($"Want '{want.Id}' conflicts with unknown want '{other}'.");

                if (!target.ConflictsWith.Contains(want.Id))
                {
                    throw new InvalidOperationException(
                        $"Want '{want.Id}' conflicts with '{other}', but not the other way round. A conflict is between two wants, so both must list it.");
                }
            }
        }

        foreach (var profile in Contrasts)
        {
            ValidateProfile(profile);
        }
    }

    /// <summary>Rules that depend on the pack a cast is rendered with.</summary>
    public void ValidateAgainst(StylePack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);

        foreach (var end in Temper.SelectMany(a => a.Ends))
        {
            if (!pack.Expressions.Keys.Any(k => string.Equals(k, end.Resting, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Temper end '{end.Id}' rests on expression '{end.Resting}', which style pack '{pack.Id}' does not define.");
            }
        }

        foreach (var (subjectKey, subject) in pack.Subjects)
        {
            var aesthetics = subject.Aesthetics?.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

            // Every member, main LI included, needs its own aesthetic when profiles move it.
            var aestheticMoves = Contrasts.Count(c => c.Look.Contains(LookDimensions.Aesthetic));
            if (aestheticMoves > 0 && aesthetics.Count < aestheticMoves + 1)
            {
                throw new InvalidOperationException(
                    $"Subject '{subjectKey}' in style pack '{pack.Id}' offers {aesthetics.Count} aesthetics, but " +
                    $"{aestheticMoves} profiles each need a different one from the main LI and each other.");
            }

            foreach (var end in Temper.SelectMany(a => a.Ends))
            {
                foreach (var leaning in end.Aesthetics)
                {
                    if (aesthetics.Count > 0 && !aesthetics.Contains(leaning))
                    {
                        throw new InvalidOperationException(
                            $"Temper end '{end.Id}' leans towards aesthetic '{leaning}', which subject '{subjectKey}' in style pack '{pack.Id}' does not offer.");
                    }
                }
            }

            if (Contrasts.Any(c => c.Look.Contains(LookDimensions.Feature)) && (subject.CastFeatures?.Count ?? 0) < 2)
            {
                throw new InvalidOperationException(
                    $"Subject '{subjectKey}' in style pack '{pack.Id}' needs at least two castFeatures for profiles that add a feature.");
            }
        }
    }

    private static void ValidateProfile(ContrastProfile profile)
    {
        foreach (var dimension in profile.Look)
        {
            if (!LookDimensions.All.Contains(dimension, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Contrast profile '{profile.Id}' moves unknown look dimension '{dimension}'. Known: {string.Join(", ", LookDimensions.All)}.");
            }
        }

        Unique(profile.Look, $"look dimension in profile '{profile.Id}'");

        if (profile.Look.Count < MinimumLookChanges)
        {
            throw new InvalidOperationException(
                $"Contrast profile '{profile.Id}' moves {profile.Look.Count} look dimensions; a variant must differ in at least {MinimumLookChanges}.");
        }

        if (!profile.Look.Any(d => LookDimensions.Silhouette.Contains(d, StringComparer.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Contrast profile '{profile.Id}' moves nothing visible in silhouette ({string.Join(", ", LookDimensions.Silhouette)}).");
        }

        var movesAge = profile.Look.Contains(LookDimensions.Age);
        if (movesAge != (profile.Age is AgeRule.DifferentBand))
        {
            throw new InvalidOperationException(
                $"Contrast profile '{profile.Id}' must list 'age' in its look exactly when its age rule is DifferentBand.");
        }

        if (profile.TemperFlips.Axes is not null && profile.TemperFlips.Count is not null)
        {
            throw new InvalidOperationException($"Contrast profile '{profile.Id}' names temper axes and a count; use one.");
        }

        if (profile.TemperFlips.Total < MinimumTemperFlips)
        {
            throw new InvalidOperationException(
                $"Contrast profile '{profile.Id}' flips {profile.TemperFlips.Total} temper axes; a variant must flip at least {MinimumTemperFlips}.");
        }
    }

    private static void Unique(IEnumerable<string> ids, string what)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new InvalidOperationException($"A {what} has a blank id.");
            }

            if (!seen.Add(id))
            {
                throw new InvalidOperationException($"The {what} id '{id}' is used twice.");
            }
        }
    }

    private static T Read<T>(string path)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Cast content '{path}' not found.", path);
        }

        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)
            ?? throw new InvalidOperationException($"Cast content '{path}' deserialised to null.");
    }
}
