using Game.Core.Cast;
using Game.Core.Characters;
using Game.Core.Content;
using Game.Core.Encounters;
using Game.Core.Scenes;
using Game.Core.Settings;
using Game.Core.Story;
using Game.Core.World;

namespace Game.Core.Tests;

/// <summary>
/// Story state with no LLM (plan build step 6): wants and scoring, facts and knowledge, schedules
/// and promises, and scene validation, played through authored scenes with the shipped content.
/// </summary>
public class StoryTests
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

    private static StoryContent Shipped() =>
        StoryContent.Load(ContentPath("values.json"), ContentPath("predicates.json"), ContentPath("relationship.json"));

    private static CastContent Cast() =>
        CastContent.Load(ContentPath("temper.json"), ContentPath("wants.json"), ContentPath("contrasts.json"));

    private static StoryProfile Profile() => new(
        [new("honesty", 3), new("humour", 2), new("kindness", 1)],
        Aversion: "ambition",
        Dealbreakers: ["dishonesty"],
        Need: "be-seen",
        LikedPlaceTypes: ["cafe", "park"],
        DislikedPlaceTypes: ["bar"]);

    private static IReadOnlyList<CastMember> Members()
    {
        var look = new CharacterAppearance("female", 24, "brown eyes", "black hair", "short hair", "fair skin", "an average build", "average height", "");
        return
        [
            new(null, look, "", Neutral, "open-a-bakery", 1, []),
            new("bolder", look, "", Neutral, "finish-a-novel", 2, []),
            new("opposite", look, "", Neutral, "leave-town", 3, []),
            new("other-life", look, "", Neutral, "save-the-shelter", 4, []),
        ];
    }

    private static readonly string[] PlaceTypes = ["cafe", "park", "bar", "bookshop", "rooftop"];

    // -------------------------------------------------------------------- content

    [Fact]
    public void Shipped_story_content_loads_and_fits_the_cast()
    {
        var story = Shipped();

        story.ValidateAgainst(Cast());

        Assert.All(FactLedger.AppearanceFacts("c1", Members()[0].Appearance), f =>
            Assert.Equal(new PredicateDefinition(f.Predicate, false, false), story.Predicate(f.Predicate)));
    }

    [Fact]
    public void Every_shipped_choice_tag_is_in_the_vocabulary()
    {
        var story = Shipped();
        var cast = Cast();
        var settings = new JsonSettingCatalog(ContentPath("settings"), new JsonLocationCatalog(ContentPath("place-types.json")));
        var encounters = new JsonEncounterCatalog(ContentPath("encounters"), settings);

        var tags = settings.All()
            .SelectMany(s => encounters.For(s))
            .SelectMany(e => e.Choices ?? [])
            .SelectMany(c => c.Tags ?? [])
            .ToList();

        Assert.NotEmpty(tags);
        Assert.All(tags, tag => Assert.True(story.IsKnownTag(tag, cast), $"'{tag}' is not in the story vocabulary."));
    }

    [Fact]
    public void A_temper_modifier_for_an_unknown_end_is_refused()
    {
        var story = Shipped();
        var broken = story with { Rules = story.Rules with { Temper = [new TemperModifier("grumpy", Affection: 2)] } };

        var ex = Assert.Throws<InvalidOperationException>(() => broken.ValidateAgainst(Cast()));

        Assert.Contains("grumpy", ex.Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------- profiles

    [Fact]
    public void Profiles_give_everyone_their_own_top_desire_and_avert_the_next_ones()
    {
        var story = Shipped();

        foreach (var seed in Enumerable.Range(0, 40).Select(i => (long)(i * 7919 + 3)))
        {
            var profiles = StoryProfileGenerator.For(Members(), story, PlaceTypes, seed);

            Assert.Equal(4, profiles.Select(p => p.Desires[0].Id).Distinct().Count());

            for (var i = 0; i < profiles.Count; i++)
            {
                var profile = profiles[i];
                Assert.Equal(story.Rules.DesireWeights, profile.Desires.Select(d => d.Weight));
                Assert.Equal(profile.Desires.Count, profile.Desires.Select(d => d.Id).Distinct().Count());
                Assert.Equal(profiles[(i + 1) % profiles.Count].Desires[0].Id, profile.Aversion);
                Assert.DoesNotContain(profile.Desires, d => d.Id == profile.Aversion);
                Assert.InRange(profile.Dealbreakers.Count, 1, 2);
                Assert.Equal(2, profile.LikedPlaceTypes.Count);
                Assert.Single(profile.DislikedPlaceTypes);
                Assert.Empty(profile.LikedPlaceTypes.Intersect(profile.DislikedPlaceTypes));
            }
        }
    }

    [Fact]
    public void The_same_seed_builds_the_same_profiles()
    {
        var story = Shipped();

        string Render(IReadOnlyList<StoryProfile> profiles) => string.Join(" | ", profiles.Select(p =>
            $"{string.Join(",", p.Desires)} {p.Aversion} {string.Join(",", p.Dealbreakers)} {p.Need} {string.Join(",", p.LikedPlaceTypes)} {string.Join(",", p.DislikedPlaceTypes)}"));

        Assert.Equal(
            Render(StoryProfileGenerator.For(Members(), story, PlaceTypes, 42)),
            Render(StoryProfileGenerator.For(Members(), story, PlaceTypes, 42)));
    }

    // -------------------------------------------------------------------- scoring

    /// <summary>Plan §6: the characters' wants differ, so one choice raises one character and costs another.</summary>
    [Fact]
    public void One_choice_raises_one_member_and_costs_another()
    {
        var story = Shipped();
        var engine = new RelationshipEngine(story);
        var members = Members();
        var profiles = StoryProfileGenerator.For(members, story, PlaceTypes, 7);

        var tag = profiles[0].Desires[0].Id;
        int Affection(int i) => engine.Score(profiles[i], Neutral, members[i].WantId, [tag]).Affection;

        Assert.True(Affection(0) > 0);
        Assert.True(Affection(3) < 0, "the member who averts the main LI's top desire should lose affection");
    }

    [Fact]
    public void Desires_aversions_wants_and_honesty_score_as_the_rules_say()
    {
        var story = Shipped();
        var rules = story.Rules;
        var engine = new RelationshipEngine(story);
        var profile = Profile();

        var humour = engine.Score(profile, Neutral, "open-a-bakery", ["humour"]);
        Assert.Equal(2 * rules.TagValue, humour.Affection);

        var aversion = engine.Score(profile, Neutral, "open-a-bakery", ["ambition"]);
        Assert.Equal(-rules.AversionWeight * rules.TagValue, aversion.Affection);

        Assert.Equal(rules.WantHelp, engine.Score(profile, Neutral, "open-a-bakery", ["helps:open-a-bakery"]).Affection);
        Assert.Equal(-rules.WantHelp, engine.Score(profile, Neutral, "open-a-bakery", ["hinders:open-a-bakery"]).Affection);
        Assert.Equal(0, engine.Score(profile, Neutral, "open-a-bakery", ["helps:leave-town"]).Affection);

        Assert.Equal(rules.TagValue, engine.Score(profile, Neutral, "open-a-bakery", ["honesty"]).Trust);
    }

    [Fact]
    public void A_fiery_temper_swings_harder_than_a_calm_one()
    {
        var engine = new RelationshipEngine(Shipped());
        var fiery = new Dictionary<string, string> { ["temper"] = "fiery" };
        var calm = new Dictionary<string, string> { ["temper"] = "calm" };

        Assert.True(
            engine.Score(Profile(), fiery, "", ["humour"]).Affection > engine.Score(Profile(), calm, "", ["humour"]).Affection);
    }

    [Fact]
    public void Affection_is_clamped_per_scene_and_per_day()
    {
        var story = Shipped();
        var rules = story.Rules;
        var engine = new RelationshipEngine(story);
        var huge = new RelationshipDelta(Affection: 100, Trust: 100);

        var state = engine.Apply(RelationshipState.Start, huge, day: 1);
        Assert.Equal(rules.PerScene, state.Affection);
        Assert.Equal(rules.PerScene, state.Trust);

        for (var scene = 0; scene < 5; scene++)
        {
            state = engine.Apply(state, huge, day: 1);
        }

        Assert.Equal(rules.PerDay, state.Affection);

        state = engine.Apply(state, huge, day: 2);
        Assert.Equal(rules.PerDay + rules.PerScene, state.Affection);
    }

    [Fact]
    public void A_dealbreaker_is_uncapped_and_only_counts_for_who_holds_it()
    {
        var story = Shipped();
        var rules = story.Rules;
        var engine = new RelationshipEngine(story);
        var warm = RelationshipState.Start with { Affection = 50, Trust = 50 };

        var lied = engine.Apply(warm, engine.Score(Profile(), Neutral, "", ["lie"]), day: 3);
        Assert.Equal(50 - rules.DealbreakerAffection, lied.Affection);
        Assert.Equal(50 - rules.DealbreakerTrust, lied.Trust);
        Assert.True(lied.Dealbreaker);

        var indifferent = Profile() with { Dealbreakers = ["cruelty"] };
        var shrug = engine.Apply(warm, engine.Score(indifferent, Neutral, "", ["lie"]), day: 3);
        Assert.Equal(warm with { GainDay = 3 }, shrug);
    }

    [Fact]
    public void A_date_at_a_liked_place_raises_attraction_and_a_disliked_one_lowers_it()
    {
        var story = Shipped();
        var engine = new RelationshipEngine(story);

        Assert.Equal(story.Rules.LikeHit, engine.DateAt(RelationshipState.Start, Profile(), "cafe", 5).Attraction);
        Assert.Equal(-story.Rules.LikeHit, engine.DateAt(RelationshipState.Start, Profile(), "bar", 5).Attraction);
        Assert.Equal(0, engine.DateAt(RelationshipState.Start, Profile(), "rooftop", 5).Attraction);
    }

    // -------------------------------------------------------------------- stages

    [Fact]
    public void Stages_advance_one_requirement_at_a_time_and_never_skip()
    {
        var story = Shipped();
        var stages = story.Rules.Stages;
        var engine = new RelationshipEngine(story);

        var met = new StageFacts(Met: true, WantRevealed: false, AcceptedDate: false, CrisisResolved: false, NeedAddressed: false);
        Assert.Equal(RelationshipStage.Acquaintance, engine.Advance(RelationshipState.Start, met, Neutral).Stage);

        // Everything but the want being revealed: the stage stops at acquaintance however high the numbers.
        var maxed = RelationshipState.Start with { Affection = 100, Trust = 100, Attraction = 100 };
        var noReveal = new StageFacts(true, false, true, true, true);
        Assert.Equal(RelationshipStage.Acquaintance, engine.Advance(maxed, noReveal, Neutral).Stage);

        var revealed = met with { WantRevealed = true };
        Assert.Equal(RelationshipStage.Acquaintance, engine.Advance(RelationshipState.Start, revealed, Neutral).Stage);

        var fond = RelationshipState.Start with { Affection = stages.FriendAffection };
        Assert.Equal(RelationshipStage.Friend, engine.Advance(fond, revealed, Neutral).Stage);

        var guarded = new Dictionary<string, string> { ["warmth"] = "guarded" };
        Assert.Equal(RelationshipStage.Acquaintance, engine.Advance(fond, revealed, guarded).Stage);

        var all = new StageFacts(true, true, true, true, true);
        Assert.Equal(RelationshipStage.Committed, engine.Advance(maxed, all, Neutral).Stage);
        Assert.Equal(RelationshipStage.Dating, engine.Advance(maxed with { Trust = 0 }, all, Neutral).Stage);
    }

    [Fact]
    public void Stage_facts_are_read_from_the_characters_flags()
    {
        var facts = StageFacts.FromFlags(new Dictionary<string, string> { ["main_li.met"] = "true", ["main_li.first_date"] = "true" }, "main_li");

        Assert.Equal(new StageFacts(true, false, true, false, false), facts);
    }

    // -------------------------------------------------------------------- facts

    private static KnownFact Known(long id, string subject, string predicate, string value, FactLevel level = FactLevel.Established, params string[] knowers) =>
        new(id, new Fact(subject, predicate, value, level, "test", 1), knowers.ToHashSet());

    [Fact]
    public void An_immutable_fact_cannot_be_contradicted()
    {
        var story = Shipped();
        var held = new[] { Known(1, "mira", "hair-color", "red hair", FactLevel.Core) };
        var proposed = new Fact("mira", "hair-color", "black hair", FactLevel.Established, "scene", 3);

        var check = FactLedger.Check(held, proposed, story.Predicate("hair-color"), explainedBy: "dyed it");

        Assert.Equal(FactVerdict.Rejected, check.Verdict);
        Assert.Contains("cannot change", check.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mutable_fact_changes_only_with_an_event_and_supersedes_the_old_one()
    {
        var story = Shipped();
        var held = new[] { Known(1, "mira", "works-as", "barista") };
        var proposed = new Fact("mira", "works-as", "florist", FactLevel.Established, "scene", 9);

        Assert.Equal(FactVerdict.Rejected, FactLedger.Check(held, proposed, story.Predicate("works-as"), null).Verdict);

        var check = FactLedger.Check(held, proposed, story.Predicate("works-as"), "quit the cafe");
        Assert.Equal(FactVerdict.Superseded, check.Verdict);
        Assert.Equal([1L], check.Supersedes);
    }

    [Fact]
    public void Multi_valued_facts_add_claims_may_be_false_and_repeats_are_duplicates()
    {
        var story = Shipped();
        var held = new[] { Known(1, "mira", "likes", "jazz"), Known(2, "mira", "hair-color", "red hair", FactLevel.Core) };

        Assert.Equal(FactVerdict.Accepted,
            FactLedger.Check(held, new Fact("mira", "likes", "rain", FactLevel.Established, "scene", 2), story.Predicate("likes"), null).Verdict);

        Assert.Equal(FactVerdict.Accepted,
            FactLedger.Check(held, new Fact("mira", "hair-color", "blonde hair", FactLevel.Claimed, "gossip", 2), story.Predicate("hair-color"), null).Verdict);

        var repeat = FactLedger.Check(held, new Fact("mira", "likes", "jazz", FactLevel.Established, "scene", 4), story.Predicate("likes"), null);
        Assert.Equal(FactVerdict.Duplicate, repeat.Verdict);
        Assert.Equal(1L, repeat.ExistingId);

        Assert.Equal(FactVerdict.Rejected,
            FactLedger.Check(held, new Fact("mira", "owns", "a boat", FactLevel.Established, "scene", 2), story.Predicate("owns"), null).Verdict);
    }

    [Fact]
    public void A_claim_shown_on_screen_is_confirmed()
    {
        var story = Shipped();
        var held = new[] { Known(5, "sam", "works-as", "pilot", FactLevel.Claimed) };

        var check = FactLedger.Check(held, new Fact("sam", "works-as", "pilot", FactLevel.Established, "scene", 6), story.Predicate("works-as"), null);

        Assert.Equal(FactVerdict.Superseded, check.Verdict);
        Assert.Equal([5L], check.Supersedes);
    }

    // -------------------------------------------------------------------- knowledge

    [Fact]
    public void A_scene_gets_what_the_player_or_someone_present_knows()
    {
        var facts = new[]
        {
            Known(1, "mira", "likes", "jazz", FactLevel.Established, FactLedger.Player),
            Known(2, "mira", "has-secret", "left a band", FactLevel.Core, "mira"),
            Known(3, "sam", "has-secret", "owes money", FactLevel.Core, "sam"),
        };

        var scene = Knowledge.ForScene(facts, ["mira"]);

        Assert.Equal([1L, 2L], scene.Select(f => f.Id));
    }

    [Fact]
    public void Learning_the_player_sees_someone_else_raises_suspicion_by_temper()
    {
        var engine = new RelationshipEngine(Shipped());
        var seeingSam = new Fact(FactLedger.Player, StoryContent.SeeingPredicate, "sam", FactLevel.Established, "scene", 8);
        var seeingMira = seeingSam with { Object = "mira" };

        var fiery = engine.Learn(RelationshipState.Start, new Dictionary<string, string> { ["temper"] = "fiery" }, seeingSam, "mira", 8);
        var calm = engine.Learn(RelationshipState.Start, new Dictionary<string, string> { ["temper"] = "calm" }, seeingSam, "mira", 8);

        Assert.True(fiery.Suspicion > calm.Suspicion && calm.Suspicion > 0);
        Assert.Equal(0, engine.Learn(RelationshipState.Start, Neutral, seeingMira, "mira", 8).Suspicion);
    }

    // -------------------------------------------------------------------- schedules and promises

    private static CharacterSchedule MiraSchedule() => new("mira",
    [
        new ScheduleEntry(TimeOfDay.Morning, "corner-cafe", [0, 1, 2, 3, 4]),
        new ScheduleEntry(TimeOfDay.Morning, "riverside-park", [5, 6]),
        new ScheduleEntry(TimeOfDay.Evening, "rooftop-garden"),
    ]);

    [Fact]
    public void A_schedule_resolves_to_a_place_per_slot_and_weekday()
    {
        var schedule = MiraSchedule();

        Assert.Equal("corner-cafe", schedule.Where(new ClockState(1, TimeOfDay.Morning)));
        Assert.Equal("riverside-park", schedule.Where(new ClockState(6, TimeOfDay.Morning)));
        Assert.Equal("corner-cafe", schedule.Where(new ClockState(8, TimeOfDay.Morning)));
        Assert.Equal("rooftop-garden", schedule.Where(new ClockState(3, TimeOfDay.Evening)));
        Assert.Null(schedule.Where(new ClockState(3, TimeOfDay.Night)));
    }

    [Fact]
    public void A_meeting_is_kept_by_showing_up_and_broken_once_its_time_passes()
    {
        var engine = new RelationshipEngine(Shipped());
        var meet = new Promise("p1", "mira", PromiseKind.Meet, MadeDay: 2, DueDay: 3, TimeOfDay.Evening, "riverside-park");
        string[] withMira = ["mira"];

        Assert.Equal(PromiseStatus.Kept, Promises.Resolve(meet, new ClockState(3, TimeOfDay.Evening), "riverside-park", withMira));
        Assert.Null(Promises.Resolve(meet, new ClockState(3, TimeOfDay.Evening), "corner-cafe", []));
        Assert.Null(Promises.Resolve(meet, new ClockState(3, TimeOfDay.Morning), "corner-cafe", []));
        Assert.Equal(PromiseStatus.Broken, Promises.Resolve(meet, new ClockState(3, TimeOfDay.Night), "corner-cafe", []));
        Assert.Null(Promises.Resolve(meet with { Status = PromiseStatus.Kept }, new ClockState(9, TimeOfDay.Night), "corner-cafe", []));

        // Breaking a promise costs more trust than keeping one earns.
        var kept = engine.PromiseResolved(RelationshipState.Start, Neutral, PromiseStatus.Kept, 3).Trust;
        var broken = engine.PromiseResolved(RelationshipState.Start, Neutral, PromiseStatus.Broken, 3).Trust;
        Assert.True(kept > 0);
        Assert.True(-broken > kept);
    }

    // -------------------------------------------------------------------- validation

    private static SceneWorld World(IReadOnlyList<Promise>? promises = null, IReadOnlyCollection<string>? summoned = null) => new(
        [
            Known(1, "mira", "works-as", "barista", FactLevel.Established, FactLedger.Player, "mira"),
            Known(2, "mira", "has-secret", "left a band", FactLevel.Core, "mira"),
            Known(3, "mira", "hair-color", "red hair", FactLevel.Core, FactLedger.Player, "mira"),
        ],
        new Dictionary<string, CharacterSchedule> { ["mira"] = MiraSchedule() },
        promises ?? [],
        new Dictionary<string, RelationshipStage> { ["mira"] = RelationshipStage.Acquaintance },
        summoned);

    private static SceneProposal AtTheCafe() =>
        new(new ClockState(2, TimeOfDay.Morning), "corner-cafe", [FactLedger.Player, "mira"]);

    [Fact]
    public void A_scene_that_fits_the_state_passes()
    {
        var validator = new SceneValidator(Shipped(), Cast());

        var scene = AtTheCafe() with
        {
            Facts = [new ProposedFact(new Fact("mira", "likes", "jazz", FactLevel.Established, "scene", 2), [FactLedger.Player, "mira"])],
            Uses = [new KnowledgeUse("mira", "mira", "has-secret", "left a band")],
            ChoiceTags = ["humour", "helps:open-a-bakery"],
            Implies = new Dictionary<string, RelationshipStage> { ["mira"] = RelationshipStage.Acquaintance },
        };

        Assert.Empty(validator.Validate(scene, World()));
    }

    [Fact]
    public void Someone_off_their_schedule_is_refused_unless_an_encounter_or_a_promise_puts_them_there()
    {
        var validator = new SceneValidator(Shipped(), Cast());
        var atThePark = AtTheCafe() with { PlaceId = "riverside-park" };

        Assert.Contains(validator.Validate(atThePark, World()), r => r.Contains("'mira' is at 'corner-cafe'", StringComparison.Ordinal));

        Assert.Empty(validator.Validate(atThePark, World(summoned: ["mira"])));

        var meet = new Promise("p1", "mira", PromiseKind.Meet, 1, 2, TimeOfDay.Morning, "riverside-park");
        Assert.Empty(validator.Validate(atThePark, World(promises: [meet])));
    }

    [Fact]
    public void Knowledge_contradictions_stage_and_vocabulary_are_each_reported()
    {
        var validator = new SceneValidator(Shipped(), Cast());

        var scene = AtTheCafe() with
        {
            Facts =
            [
                new ProposedFact(new Fact("mira", "hair-color", "blue hair", FactLevel.Established, "scene", 2), [FactLedger.Player]),
                new ProposedFact(new Fact("mira", "likes", "rain", FactLevel.Established, "scene", 2), ["sam"]),
            ],
            Uses = [new KnowledgeUse(FactLedger.Player, "mira", "has-secret", "left a band")],
            ChoiceTags = ["charm"],
            Implies = new Dictionary<string, RelationshipStage> { ["mira"] = RelationshipStage.Dating },
        };

        var reasons = validator.Validate(scene with { Present = [FactLedger.Player, "mira"] }, World());

        Assert.Contains(reasons, r => r.Contains("cannot change", StringComparison.Ordinal));
        Assert.Contains(reasons, r => r.Contains("'sam' learns", StringComparison.Ordinal));
        Assert.Contains(reasons, r => r.Contains("'player' uses 'mira has-secret left a band', which they do not know", StringComparison.Ordinal));
        Assert.Contains(reasons, r => r.Contains("'charm'", StringComparison.Ordinal));
        Assert.Contains(reasons, r => r.Contains("as Dating, but they are Acquaintance", StringComparison.Ordinal));
    }

    [Fact]
    public void A_character_cannot_use_a_fact_they_do_not_know()
    {
        var validator = new SceneValidator(Shipped(), Cast());
        var world = World() with
        {
            Facts = [.. World().Facts, Known(4, "sam", "has-secret", "owes money", FactLevel.Core, "sam")],
        };

        var scene = AtTheCafe() with { Uses = [new KnowledgeUse("mira", "sam", "has-secret", "owes money")] };

        Assert.Contains(validator.Validate(scene, world), r => r.Contains("which they do not know", StringComparison.Ordinal));
    }
}
