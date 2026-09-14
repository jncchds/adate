using Game.Core.Encounters;

namespace Game.Core.Story;

public enum RelationshipStage
{
    Stranger,
    Acquaintance,
    Friend,
    Dating,
    Committed,
}

/// <param name="GainDay">The day <paramref name="DayGain"/> was counted on.</param>
/// <param name="DayGain">Affection gained so far that day, for the per-day clamp.</param>
/// <param name="Dealbreaker">Whether the player has ever tripped one of this character's dealbreakers.</param>
public sealed record RelationshipState(
    int Affection,
    int Trust,
    int Attraction,
    int Suspicion,
    RelationshipStage Stage,
    int GainDay = 0,
    int DayGain = 0,
    bool Dealbreaker = false)
{
    public static RelationshipState Start { get; } = new(0, 0, 0, 0, RelationshipStage.Stranger);
}

/// <param name="ExemptAffection">Change that skips the clamps: dealbreakers and promises (plan §6).</param>
public sealed record RelationshipDelta(
    int Affection = 0,
    int Trust = 0,
    int Attraction = 0,
    int Suspicion = 0,
    int ExemptAffection = 0,
    int ExemptTrust = 0,
    bool Dealbreaker = false);

/// <summary>The story facts a stage change depends on, as flags under the character's key.</summary>
public sealed record StageFacts(bool Met, bool WantRevealed, bool AcceptedDate, bool CrisisResolved, bool NeedAddressed)
{
    public static StageFacts FromFlags(IReadOnlyDictionary<string, string> flags, string who)
    {
        ArgumentNullException.ThrowIfNull(flags);

        bool Has(string what) => EncounterEvaluator.Holds(flags, $"{who}.{what}");
        return new StageFacts(Has("met"), Has("want_revealed"), Has("first_date"), Has("crisis_resolved"), Has("need_addressed"));
    }
}

/// <summary>
/// Every relationship number the game computes (plan §6). The LLM never does: it proposes tags, and
/// this turns them into changes, clamped so no single scene decides a route.
/// </summary>
public sealed class RelationshipEngine(StoryContent content)
{
    public const int Bound = 100;

    private RelationshipRules Rules => content.Rules;

    /// <summary>What a choice's tags are worth to one character, before clamping.</summary>
    public RelationshipDelta Score(
        StoryProfile profile,
        IReadOnlyDictionary<string, string> temper,
        string wantId,
        IReadOnlyList<string> tags)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(temper);
        ArgumentNullException.ThrowIfNull(tags);

        double affection = 0, trust = 0;
        int exemptAffection = 0, exemptTrust = 0;
        var dealbreaker = false;

        foreach (var tag in tags)
        {
            if (tag.StartsWith(StoryContent.HelpsPrefix, StringComparison.Ordinal))
            {
                affection += tag[StoryContent.HelpsPrefix.Length..] == wantId ? Rules.WantHelp : 0;
                continue;
            }

            if (tag.StartsWith(StoryContent.HindersPrefix, StringComparison.Ordinal))
            {
                affection -= tag[StoryContent.HindersPrefix.Length..] == wantId ? Rules.WantHelp : 0;
                continue;
            }

            if (content.DealbreakerForTag(tag) is { } broken)
            {
                if (profile.Dealbreakers.Contains(broken.Id))
                {
                    exemptAffection -= Rules.DealbreakerAffection;
                    exemptTrust -= Rules.DealbreakerTrust;
                    dealbreaker = true;
                }

                continue;
            }

            affection += profile.WeightOf(tag) * Rules.TagValue;

            if (tag == profile.Aversion)
            {
                affection -= Rules.AversionWeight * Rules.TagValue;
            }

            if (tag == StoryContent.HonestyTag)
            {
                trust += Rules.TagValue;
            }
        }

        return new RelationshipDelta(
            Affection: Round(affection * Scale(temper, m => m.Affection)),
            Trust: Round(trust * Scale(temper, m => m.Trust)),
            ExemptAffection: exemptAffection,
            ExemptTrust: exemptTrust,
            Dealbreaker: dealbreaker);
    }

    /// <summary>
    /// Applies a change. Affection, trust and attraction move at most <see cref="RelationshipRules.PerScene"/>
    /// per scene, and affection gains at most <see cref="RelationshipRules.PerDay"/> per day. Losses are
    /// only clamped per scene, and the exempt parts are not clamped at all.
    /// </summary>
    public RelationshipState Apply(RelationshipState state, RelationshipDelta delta, int day)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(delta);

        var gained = state.GainDay == day ? state.DayGain : 0;

        var affection = Math.Clamp(delta.Affection, -Rules.PerScene, Rules.PerScene);
        if (affection > 0)
        {
            affection = Math.Min(affection, Math.Max(0, Rules.PerDay - gained));
            gained += affection;
        }

        var trust = Math.Clamp(delta.Trust, -Rules.PerScene, Rules.PerScene);
        var attraction = Math.Clamp(delta.Attraction, -Rules.PerScene, Rules.PerScene);

        return state with
        {
            Affection = Bounded(state.Affection + affection + delta.ExemptAffection),
            Trust = Bounded(state.Trust + trust + delta.ExemptTrust),
            Attraction = Bounded(state.Attraction + attraction),
            Suspicion = Math.Clamp(state.Suspicion + delta.Suspicion, 0, Bound),
            GainDay = day,
            DayGain = gained,
            Dealbreaker = state.Dealbreaker || delta.Dealbreaker,
        };
    }

    /// <summary>A date at a place type the character likes raises attraction; one they dislike lowers it.</summary>
    public RelationshipState DateAt(RelationshipState state, StoryProfile profile, string placeTypeId, int day)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var attraction = profile.LikedPlaceTypes.Contains(placeTypeId) ? Rules.LikeHit
            : profile.DislikedPlaceTypes.Contains(placeTypeId) ? -Rules.LikeHit
            : 0;

        return Apply(state, new RelationshipDelta(Attraction: attraction), day);
    }

    /// <summary>
    /// A character learning a fact is a state change (plan §8). Learning the player is seeing someone
    /// else raises suspicion, by more for a fiery temper.
    /// </summary>
    public RelationshipState Learn(
        RelationshipState state,
        IReadOnlyDictionary<string, string> temper,
        Fact fact,
        string characterId,
        int day)
    {
        ArgumentNullException.ThrowIfNull(fact);

        var aboutSomeoneElse = fact.Subject == FactLedger.Player
            && fact.Predicate == StoryContent.SeeingPredicate
            && fact.Object != characterId;

        return aboutSomeoneElse
            ? Apply(state, new RelationshipDelta(Suspicion: Round(Rules.SuspicionOnLearn * Scale(temper, m => m.Suspicion))), day)
            : state;
    }

    /// <summary>A kept promise builds trust; a broken one costs more than keeping one earns.</summary>
    public RelationshipState PromiseResolved(
        RelationshipState state,
        IReadOnlyDictionary<string, string> temper,
        PromiseStatus status,
        int day)
    {
        var trustScale = Scale(temper, m => m.Trust);

        var change = status switch
        {
            PromiseStatus.Kept => Round(Rules.PromiseKept * trustScale),
            PromiseStatus.Broken => -Round(Rules.PromiseBroken * trustScale),
            _ => throw new ArgumentException("Only a kept or broken promise changes trust.", nameof(status)),
        };

        return Apply(state, new RelationshipDelta(ExemptTrust: change), day);
    }

    /// <summary>
    /// Moves the stage forward as far as the facts and numbers allow, one step at a time, so no stage
    /// is skipped. Never moves back: leaving is an ending rule, not a stage (plan §9).
    /// </summary>
    public RelationshipState Advance(RelationshipState state, StageFacts facts, IReadOnlyDictionary<string, string> temper)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(facts);

        var stages = Rules.Stages;
        var stage = state.Stage;

        while (true)
        {
            var next = stage switch
            {
                RelationshipStage.Stranger when facts.Met => RelationshipStage.Acquaintance,
                RelationshipStage.Acquaintance when facts.WantRevealed
                    && state.Affection >= ThresholdFor(stages.FriendAffection, temper) => RelationshipStage.Friend,
                RelationshipStage.Friend when facts.AcceptedDate
                    && state.Affection >= ThresholdFor(stages.DatingAffection, temper)
                    && state.Attraction >= ThresholdFor(stages.DatingAttraction, temper) => RelationshipStage.Dating,
                RelationshipStage.Dating when facts.CrisisResolved && facts.NeedAddressed
                    && state.Trust >= ThresholdFor(stages.CommittedTrust, temper) => RelationshipStage.Committed,
                _ => stage,
            };

            if (next == stage)
            {
                return state with { Stage = stage };
            }

            stage = next;
        }
    }

    /// <summary>A threshold as this temper sees it: guarded characters need more, open ones less.</summary>
    public int ThresholdFor(int value, IReadOnlyDictionary<string, string> temper) =>
        (int)Math.Ceiling(value * Scale(temper, m => m.Threshold));

    private double Scale(IReadOnlyDictionary<string, string> temper, Func<TemperModifier, double> pick) =>
        content.TemperScale(temper, pick);

    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    private static int Bounded(int value) => Math.Clamp(value, -Bound, Bound);
}
