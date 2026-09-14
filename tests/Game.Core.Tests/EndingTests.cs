using Game.Core.Scenes;
using Game.Core.Story;
using Game.Core.World;

namespace Game.Core.Tests;

/// <summary>Endings (plan §9, build step 8): leaving rules, the ending check, and every pick.</summary>
public class EndingTests
{
    private static readonly IReadOnlyDictionary<string, string> Neutral = new Dictionary<string, string>();

    private static string ContentPath(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.EnumerateFiles("*.sln").Concat(dir.EnumerateFiles("*.slnx")).Any() is false)
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("No repository root."), "content", file);
    }

    private static EndingContent Endings() => EndingContent.Load(ContentPath("endings.json"));

    private static StoryContent Story() =>
        StoryContent.Load(ContentPath("values.json"), ContentPath("predicates.json"), ContentPath("relationship.json"));

    private static EndingRules Rules() => new(Endings(), Story());

    private static RouteStatus Status(
        string key = "main_li",
        RelationshipStage stage = RelationshipStage.Acquaintance,
        bool met = true,
        string? left = null,
        int suspicion = 0,
        bool dealbreaker = false,
        int? lastSeen = 5,
        int broken = 0,
        IReadOnlyDictionary<string, string>? temper = null) =>
        new(key, met, left, RelationshipState.Start with { Stage = stage, Suspicion = suspicion, Dealbreaker = dealbreaker }, temper ?? Neutral, lastSeen, broken);

    [Fact]
    public void Shipped_endings_load()
    {
        var endings = Endings();

        Assert.All(Enum.GetValues<LeaveReason>(), r => Assert.Contains("{who}", endings.ReasonText(r), StringComparison.Ordinal));
    }

    // -------------------------------------------------------------------- leaving

    [Fact]
    public void Each_rule_makes_someone_leave_and_a_dealbreaker_comes_first()
    {
        var rules = Rules();
        var leaving = Endings().Leaving;

        Assert.Null(rules.Leaving(Status(), today: 6));
        Assert.Equal(LeaveReason.Dealbreaker, rules.Leaving(Status(dealbreaker: true, suspicion: 100, broken: 9), 6));
        Assert.Equal(LeaveReason.Suspicion, rules.Leaving(Status(suspicion: leaving.SuspicionTolerance), 6));
        Assert.Equal(LeaveReason.BrokenPromises, rules.Leaving(Status(broken: leaving.BrokenPromises), 6));
        Assert.Equal(LeaveReason.Neglect, rules.Leaving(Status(lastSeen: 2), 2 + leaving.NeglectDays));
    }

    [Fact]
    public void Neglect_only_ends_an_acquaintance_and_only_after_the_full_gap()
    {
        var rules = Rules();
        var gap = Endings().Leaving.NeglectDays;

        Assert.Null(rules.Leaving(Status(lastSeen: 2), 2 + gap - 1));
        Assert.Null(rules.Leaving(Status(stage: RelationshipStage.Friend, lastSeen: 2), 2 + gap + 5));
        Assert.Null(rules.Leaving(Status(lastSeen: null), 20));
    }

    [Fact]
    public void Patience_follows_temper_for_neglect_and_broken_promises()
    {
        var rules = Rules();
        var leaving = Endings().Leaving;
        var fiery = new Dictionary<string, string> { ["temper"] = "fiery" };
        var patient = new Dictionary<string, string> { ["temper"] = "calm", ["drive"] = "easygoing" };

        Assert.Equal(leaving.NeglectDays, rules.NeglectDaysFor(Neutral));
        Assert.True(rules.NeglectDaysFor(fiery) < leaving.NeglectDays);
        Assert.True(rules.NeglectDaysFor(patient) > leaving.NeglectDays);
        Assert.True(rules.BrokenPromisesFor(patient) >= leaving.BrokenPromises);

        var gap = rules.NeglectDaysFor(fiery);
        Assert.Equal(LeaveReason.Neglect, rules.Leaving(Status(lastSeen: 2, temper: fiery), 2 + gap));
        Assert.Null(rules.Leaving(Status(lastSeen: 2, temper: patient), 2 + gap));
    }

    [Fact]
    public void A_fiery_temper_tolerates_less_suspicion()
    {
        var rules = Rules();
        var fiery = new Dictionary<string, string> { ["temper"] = "fiery" };
        var calm = new Dictionary<string, string> { ["temper"] = "calm" };

        Assert.True(rules.SuspicionToleranceFor(fiery) < rules.SuspicionToleranceFor(Neutral));
        Assert.True(rules.SuspicionToleranceFor(calm) > rules.SuspicionToleranceFor(Neutral));
    }

    [Fact]
    public void Someone_who_left_or_was_never_met_does_not_leave_again()
    {
        var rules = Rules();

        Assert.Null(rules.Leaving(Status(left: "Neglect", dealbreaker: true), 10));
        Assert.Null(rules.Leaving(Status(met: false, dealbreaker: true), 10));
    }

    // -------------------------------------------------------------------- the check

    [Fact]
    public void The_check_is_due_on_the_last_day_or_once_a_single_open_route_is_committed()
    {
        var committed = Status("main_li", RelationshipStage.Committed);
        var dating = Status("routine", RelationshipStage.Dating);

        Assert.True(EndingRules.IsDue(new ClockState(28, TimeOfDay.Night).Next(), 28, [dating]));
        Assert.False(EndingRules.IsDue(new ClockState(12, TimeOfDay.Evening), 28, [dating]));
        Assert.True(EndingRules.IsDue(new ClockState(12, TimeOfDay.Evening), 28, [committed, Status("routine", met: false)]));
        Assert.True(EndingRules.IsDue(new ClockState(12, TimeOfDay.Evening), 28, [committed, dating with { LeftFor = "Suspicion" }]));
        Assert.False(EndingRules.IsDue(new ClockState(12, TimeOfDay.Evening), 28, [committed, dating]));
    }

    /// <summary>
    /// Plan §9: every combination of open routes and player picks yields exactly one ending. Each of
    /// four love interests is in one of five situations, and every key plus alone is picked.
    /// </summary>
    [Fact]
    public void Every_combination_of_routes_and_picks_yields_exactly_one_ending_or_a_refusal()
    {
        string[] keys = ["main_li", "routine", "introduced", "chance"];
        Func<string, RouteStatus>[] situations =
        [
            key => Status(key, met: false),
            key => Status(key, left: "Neglect"),
            key => Status(key, RelationshipStage.Acquaintance),
            key => Status(key, RelationshipStage.Dating),
            key => Status(key, RelationshipStage.Committed),
        ];

        var combinations = 0;
        foreach (var combination in Enumerable.Range(0, (int)Math.Pow(situations.Length, keys.Length)))
        {
            var routes = keys.Select((key, i) => situations[combination / (int)Math.Pow(situations.Length, i) % situations.Length](key)).ToList();
            var offer = EndingRules.Offer(routes);
            var endings = new List<EndingChoice>();

            foreach (var pick in keys.Append(EndingRules.AloneKey))
            {
                var onOffer = pick == EndingRules.AloneKey || offer.Contains(pick);
                if (!onOffer)
                {
                    Assert.Throws<InvalidOperationException>(() => EndingRules.Resolve(routes, pick));
                    continue;
                }

                var ending = EndingRules.Resolve(routes, pick);
                Assert.Equal(ending, EndingRules.Resolve(routes, pick));
                endings.Add(ending);

                if (pick == EndingRules.AloneKey)
                {
                    var expected = offer.Count == 0 && routes.Any(r => r.LeftFor is not null) ? EndingKind.LeftAlone : EndingKind.Alone;
                    Assert.Equal(new EndingChoice(expected, null), ending);
                }
                else
                {
                    Assert.Equal(new EndingChoice(EndingKind.Together, pick), ending);
                    Assert.True(routes.Single(r => r.Key == pick) is { Open: true, State.Stage: >= RelationshipStage.Dating });
                }
            }

            Assert.Equal(offer.Count + 1, endings.Distinct().Count());
            combinations++;
        }

        Assert.Equal(625, combinations);
    }
}
