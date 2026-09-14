using System.Text.Json;
using System.Text.Json.Serialization;
using Game.Core.Cast;

namespace Game.Core.Story;

/// <summary>What a character looks for in a partner (plan §6). Choice tags name these.</summary>
public sealed record DesireDefinition(string Id, string Label);

/// <param name="Tag">The choice tag that trips this dealbreaker.</param>
public sealed record DealbreakerDefinition(string Id, string Label, string Tag);

/// <summary>What would actually make a character happy. Hidden; required to reach <c>committed</c>.</summary>
public sealed record NeedDefinition(string Id, string Label);

/// <param name="Multi">Whether a subject can hold several values at once (likes), or one (a job).</param>
/// <param name="Mutable">Whether a value can change during the story, given an event that explains it.</param>
public sealed record PredicateDefinition(string Id, bool Multi, bool Mutable);

/// <summary>How one temper end scales relationship changes and stage thresholds. 1 leaves it as is.</summary>
public sealed record TemperModifier(string End, double Affection = 1, double Trust = 1, double Suspicion = 1, double Threshold = 1);

public sealed record StageThresholds(int FriendAffection, int DatingAffection, int DatingAttraction, int CommittedTrust);

/// <param name="PerScene">The most any value moves in one scene. Dealbreakers and promises are exempt.</param>
/// <param name="PerDay">The most affection a character gains in one day, so no single day decides a route.</param>
/// <param name="DesireWeights">Weights of a character's desires, strongest first; also how many they have.</param>
/// <param name="TagValue">What one tag is worth at weight 1.</param>
/// <param name="AversionWeight">The weight a character counts against their aversion.</param>
public sealed record RelationshipRules(
    int PerScene,
    int PerDay,
    IReadOnlyList<int> DesireWeights,
    int TagValue,
    int AversionWeight,
    int WantHelp,
    int DealbreakerTrust,
    int DealbreakerAffection,
    int PromiseKept,
    int PromiseBroken,
    int LikeHit,
    int SuspicionOnLearn,
    StageThresholds Stages,
    IReadOnlyList<TemperModifier> Temper);

public sealed record StoryValues(
    IReadOnlyList<DesireDefinition> Desires,
    IReadOnlyList<DealbreakerDefinition> Dealbreakers,
    IReadOnlyList<NeedDefinition> Needs);

/// <summary>
/// The story vocabulary: partner desires, dealbreakers, needs, fact predicates and the relationship
/// rules. Content, like the cast vocabulary, so tuning a threshold is data rather than a release.
/// </summary>
public sealed record StoryContent(StoryValues Values, IReadOnlyList<PredicateDefinition> Predicates, RelationshipRules Rules)
{
    public const string HelpsPrefix = "helps:";
    public const string HindersPrefix = "hinders:";

    /// <summary>
    /// Stands for the want of the person a scene is about, in <c>helps:</c>/<c>hinders:</c> tags on
    /// generated arcs, and is replaced with their want id before scoring.
    /// </summary>
    public const string WantToken = "{want}";

    /// <summary>The tag that also builds trust (plan §6: trust rises with honesty).</summary>
    public const string HonestyTag = "honesty";

    /// <summary>The predicate that records who the player is seeing; learning it raises suspicion.</summary>
    public const string SeeingPredicate = "is-seeing";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static StoryContent Load(string valuesPath, string predicatesPath, string relationshipPath)
    {
        var content = new StoryContent(
            Read<StoryValues>(valuesPath),
            Read<List<PredicateDefinition>>(predicatesPath),
            Read<RelationshipRules>(relationshipPath));

        content.Validate();
        return content;
    }

    public PredicateDefinition? Predicate(string id) =>
        Predicates.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));

    public DealbreakerDefinition? DealbreakerForTag(string tag) =>
        Values.Dealbreakers.FirstOrDefault(d => string.Equals(d.Tag, tag, StringComparison.Ordinal));

    /// <summary>A desire id, a dealbreaker tag, or <c>helps:</c>/<c>hinders:</c> followed by a want id.</summary>
    public bool IsKnownTag(string tag, CastContent cast)
    {
        ArgumentNullException.ThrowIfNull(cast);

        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        foreach (var prefix in new[] { HelpsPrefix, HindersPrefix })
        {
            if (tag.StartsWith(prefix, StringComparison.Ordinal))
            {
                var want = tag[prefix.Length..];
                return want == WantToken || cast.Wants.Any(w => w.Id == want);
            }
        }

        return Values.Desires.Any(d => d.Id == tag) || DealbreakerForTag(tag) is not null;
    }

    public void Validate()
    {
        Unique(Values.Desires.Select(d => d.Id), "desire");
        Unique(Values.Dealbreakers.Select(d => d.Id), "dealbreaker");
        Unique(Values.Dealbreakers.Select(d => d.Tag), "dealbreaker tag");
        Unique(Values.Needs.Select(n => n.Id), "need");
        Unique(Predicates.Select(p => p.Id), "predicate");

        foreach (var dealbreaker in Values.Dealbreakers)
        {
            if (Values.Desires.Any(d => d.Id == dealbreaker.Tag) || dealbreaker.Tag.Contains(':', StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Dealbreaker '{dealbreaker.Id}' is tripped by '{dealbreaker.Tag}', which reads as a desire or a want tag.");
            }
        }

        if (Values.Dealbreakers.Count < 2 || Values.Needs.Count == 0)
        {
            throw new InvalidOperationException("Story content needs at least two dealbreakers and one need.");
        }

        if (Values.Desires.All(d => d.Id != HonestyTag))
        {
            throw new InvalidOperationException($"Story content must declare the '{HonestyTag}' desire; trust is built on it.");
        }

        if (Predicate(SeeingPredicate) is not { Multi: true })
        {
            throw new InvalidOperationException($"Story content must declare '{SeeingPredicate}' as a multi-valued predicate.");
        }

        if (Rules.DesireWeights.Count == 0 || Rules.DesireWeights.Any(w => w <= 0))
        {
            throw new InvalidOperationException("Desire weights must be a non-empty list of positive numbers.");
        }

        if (Values.Desires.Count < Rules.DesireWeights.Count + 1)
        {
            throw new InvalidOperationException(
                $"Story content declares {Values.Desires.Count} desires; a character holds {Rules.DesireWeights.Count} and averts one more.");
        }

        if (Rules.PerScene <= 0 || Rules.PerDay < Rules.PerScene)
        {
            throw new InvalidOperationException("The per-scene clamp must be positive and no larger than the per-day clamp.");
        }

        var stages = Rules.Stages;
        if (stages.FriendAffection <= 0 || stages.DatingAffection < stages.FriendAffection || stages.CommittedTrust <= 0)
        {
            throw new InvalidOperationException("Stage thresholds must be positive, and dating needs at least the affection of a friend.");
        }

        foreach (var modifier in Rules.Temper)
        {
            if (new[] { modifier.Affection, modifier.Trust, modifier.Suspicion, modifier.Threshold }.Any(s => s <= 0))
            {
                throw new InvalidOperationException($"Temper modifier for '{modifier.End}' has a scale that is not positive.");
            }
        }

        Unique(Rules.Temper.Select(m => m.End), "temper modifier");
    }

    /// <summary>Rules that depend on the cast vocabulary.</summary>
    public void ValidateAgainst(CastContent cast)
    {
        ArgumentNullException.ThrowIfNull(cast);

        var ends = cast.Temper.SelectMany(a => a.Ends).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var modifier in Rules.Temper)
        {
            if (!ends.Contains(modifier.End))
            {
                throw new InvalidOperationException($"Temper modifier names '{modifier.End}', which is not a temper end.");
            }
        }

        // Every member of the cast has their own top desire.
        var castSize = cast.Contrasts.Count + 1;
        if (Values.Desires.Count < castSize)
        {
            throw new InvalidOperationException(
                $"Story content declares {Values.Desires.Count} desires, but a cast of {castSize} each needs a different top desire.");
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
            throw new FileNotFoundException($"Story content '{path}' not found.", path);
        }

        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)
            ?? throw new InvalidOperationException($"Story content '{path}' deserialised to null.");
    }
}
