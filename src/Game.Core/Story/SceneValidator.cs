using Game.Core.Cast;
using Game.Core.World;

namespace Game.Core.Story;

public sealed record ProposedFact(Fact Fact, IReadOnlyList<string> Knowers, string? ExplainedBy = null);

/// <summary>A character in the scene acting on a fact: saying it, or behaving as if they know it.</summary>
public sealed record KnowledgeUse(string CharacterId, string Subject, string Predicate, string Object);

/// <summary>What a written scene claims happened: the JSON half of the LLM's answer (plan §8).</summary>
/// <param name="Implies">The stage the scene treats each character as being at.</param>
public sealed record SceneProposal(
    ClockState Clock,
    string PlaceId,
    IReadOnlyList<string> Present,
    IReadOnlyList<ProposedFact>? Facts = null,
    IReadOnlyList<KnowledgeUse>? Uses = null,
    IReadOnlyList<string>? ChoiceTags = null,
    IReadOnlyDictionary<string, RelationshipStage>? Implies = null);

/// <param name="Summoned">Characters the encounter itself puts in the scene, whatever their schedule.</param>
public sealed record SceneWorld(
    IReadOnlyList<KnownFact> Facts,
    IReadOnlyDictionary<string, CharacterSchedule> Schedules,
    IReadOnlyList<Promise> Promises,
    IReadOnlyDictionary<string, RelationshipStage> Stages,
    IReadOnlyCollection<string>? Summoned = null);

/// <summary>
/// Checks a scene against the state C# owns before any of it is stored (plan §8). Each reason is
/// written to be handed back to the writer on a retry.
/// </summary>
public sealed class SceneValidator(StoryContent story, CastContent cast)
{
    public IReadOnlyList<string> Validate(SceneProposal scene, SceneWorld world)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(world);

        var reasons = new List<string>();
        var present = scene.Present.ToHashSet(StringComparer.Ordinal);

        foreach (var tag in scene.ChoiceTags ?? [])
        {
            if (!story.IsKnownTag(tag, cast))
            {
                reasons.Add($"The choice tag '{tag}' is not in the vocabulary.");
            }
        }

        // Facts are checked in order, each against the ones before it in the same scene too.
        var facts = world.Facts.ToList();
        foreach (var proposed in scene.Facts ?? [])
        {
            var check = FactLedger.Check(facts, proposed.Fact, story.Predicate(proposed.Fact.Predicate), proposed.ExplainedBy);
            if (check.Verdict is FactVerdict.Rejected)
            {
                reasons.Add(check.Reason!);
                continue;
            }

            foreach (var knower in proposed.Knowers)
            {
                if (knower != FactLedger.Player && !present.Contains(knower))
                {
                    reasons.Add($"'{knower}' learns '{Describe(proposed.Fact)}' in a scene they are not in.");
                }
            }

            facts.RemoveAll(f => check.Supersedes.Contains(f.Id));
            facts.Add(new KnownFact(0, proposed.Fact, proposed.Knowers.ToHashSet(StringComparer.Ordinal)));
        }

        foreach (var who in present)
        {
            if (who == FactLedger.Player
                || (world.Summoned?.Contains(who) ?? false)
                || world.Promises.Any(p => p.CharacterId == who && Promises.PutsThere(p, scene.Clock, scene.PlaceId)))
            {
                continue;
            }

            // A character with no schedule yet is not constrained by one.
            if (world.Schedules.TryGetValue(who, out var schedule) && schedule.Where(scene.Clock) is var where && where != scene.PlaceId)
            {
                reasons.Add(where is null
                    ? $"'{who}' has nowhere to be at {scene.Clock.Slot} on day {scene.Clock.Day}, so they cannot be at '{scene.PlaceId}'."
                    : $"'{who}' is at '{where}' at {scene.Clock.Slot} on day {scene.Clock.Day}, not at '{scene.PlaceId}'.");
            }
        }

        foreach (var use in scene.Uses ?? [])
        {
            if (!present.Contains(use.CharacterId))
            {
                reasons.Add($"'{use.CharacterId}' acts in a scene they are not in.");
            }
            else if (!Knowledge.Knows(facts, use.CharacterId, use.Subject, use.Predicate, use.Object))
            {
                reasons.Add($"'{use.CharacterId}' uses '{use.Subject} {use.Predicate} {use.Object}', which they do not know.");
            }
        }

        foreach (var (who, implied) in scene.Implies ?? new Dictionary<string, RelationshipStage>())
        {
            var actual = world.Stages.TryGetValue(who, out var stage) ? stage : RelationshipStage.Stranger;
            if (implied > actual)
            {
                reasons.Add($"The scene treats '{who}' as {implied}, but they are {actual}.");
            }
        }

        return reasons;
    }

    private static string Describe(Fact fact) => $"{fact.Subject} {fact.Predicate} {fact.Object}";
}
