using Game.Core;
using Game.Core.Characters;
using Game.Core.Saves;
using Game.Core.Scenes;
using Game.Core.Story;
using Game.Core.World;
using Game.Data.Repositories;
using Microsoft.Data.Sqlite;

namespace Game.Data.Tests;

public class StoryStateTests
{
    private static readonly PredicateDefinition HairColor = new("hair-color", Multi: false, Mutable: false);
    private static readonly PredicateDefinition WorksAs = new("works-as", Multi: false, Mutable: true);

    private static CharacterAppearance Appearance() => new(
        Subject: "female",
        Age: 24,
        EyeColor: "green eyes",
        HairColor: "red hair",
        HairStyle: "long hair",
        SkinTone: "pale skin",
        Build: "slim",
        Height: "tall",
        DistinguishingFeature: "freckles");

    private static async Task<(SaveId Save, Guid Character)> NewSaveAsync(TempDatabase db)
    {
        var save = await new SaveRepository(db.Database).CreateAsync("zimage-anime", "fingerprint", Ceiling.PG13);
        var character = await new CharacterRepository(db.Database).CreateAsync(save.Id, Appearance(), "Mira");
        return (save.Id, character.Id);
    }

    private static Fact Fact(string subject, string predicate, string value, int day = 1, FactLevel level = FactLevel.Established) =>
        new(subject, predicate, value, level, "scene", day);

    [Fact]
    public async Task Facts_are_checked_stored_superseded_and_known()
    {
        using var db = new TempDatabase();
        var story = new StoryStateRepository(db.Database);
        var (save, mira) = await NewSaveAsync(db);
        var id = mira.ToString();

        Assert.Equal(FactVerdict.Accepted,
            (await story.AddFactAsync(save, Fact(id, "hair-color", "red hair", 0, FactLevel.Core), HairColor, [id])).Verdict);

        var contradiction = await story.AddFactAsync(save, Fact(id, "hair-color", "black hair", 3), HairColor, [FactLedger.Player]);
        Assert.Equal(FactVerdict.Rejected, contradiction.Verdict);

        await story.AddFactAsync(save, Fact(id, "works-as", "barista"), WorksAs, [FactLedger.Player, id]);
        Assert.Equal(FactVerdict.Rejected, (await story.AddFactAsync(save, Fact(id, "works-as", "florist", 9), WorksAs, [id])).Verdict);

        var changed = await story.AddFactAsync(save, Fact(id, "works-as", "florist", 9), WorksAs, [id], explainedBy: "quit the cafe");
        Assert.Equal(FactVerdict.Superseded, changed.Verdict);

        var facts = await story.GetFactsAsync(save);
        Assert.Equal(["red hair", "florist"], facts.Select(f => f.Fact.Object));

        var florist = facts.Single(f => f.Fact.Object == "florist");
        Assert.DoesNotContain(FactLedger.Player, florist.Knowers);

        Assert.True(await story.LearnAsync(save, florist.Id, FactLedger.Player, 10));
        Assert.False(await story.LearnAsync(save, florist.Id, FactLedger.Player, 11));

        // Hearing a fact already held stores nothing new, but the listener learns it.
        var repeat = await story.AddFactAsync(save, Fact(id, "hair-color", "red hair", 4), HairColor, [FactLedger.Player]);
        Assert.Equal(FactVerdict.Duplicate, repeat.Verdict);

        facts = await story.GetFactsAsync(save);
        Assert.Equal(2, facts.Count);
        Assert.All(facts, f => Assert.Contains(FactLedger.Player, f.Knowers));
    }

    [Fact]
    public async Task Facts_are_append_only_in_the_schema()
    {
        using var db = new TempDatabase();
        var story = new StoryStateRepository(db.Database);
        var (save, mira) = await NewSaveAsync(db);
        var id = mira.ToString();

        await story.AddFactAsync(save, Fact(id, "works-as", "barista"), WorksAs, [id]);
        await story.AddFactAsync(save, Fact(id, "works-as", "florist", 5), WorksAs, [id], explainedBy: "new job");

        using var connection = db.Database.Open();

        foreach (var sql in new[]
                 {
                     "UPDATE fact SET object = 'pilot' WHERE object = 'florist';",
                     "UPDATE fact SET superseded_by = NULL WHERE superseded_by IS NOT NULL;",
                 })
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        }
    }

    [Fact]
    public async Task Relationships_round_trip_and_a_stage_never_moves_back()
    {
        using var db = new TempDatabase();
        var story = new StoryStateRepository(db.Database);
        var (save, mira) = await NewSaveAsync(db);

        Assert.Equal(RelationshipState.Start, await story.GetRelationshipAsync(save, mira));

        var friend = new RelationshipState(14, 6, 3, 10, RelationshipStage.Friend, GainDay: 4, DayGain: 6, Dealbreaker: true);
        await story.SaveRelationshipAsync(save, mira, friend);
        Assert.Equal(friend, await story.GetRelationshipAsync(save, mira));

        await Assert.ThrowsAsync<SqliteException>(() =>
            story.SaveRelationshipAsync(save, mira, friend with { Stage = RelationshipStage.Acquaintance }));
    }

    [Fact]
    public async Task A_profile_is_stored_once()
    {
        using var db = new TempDatabase();
        var story = new StoryStateRepository(db.Database);
        var (_, mira) = await NewSaveAsync(db);

        var first = new StoryProfile([new("honesty", 3), new("humour", 2)], "ambition", ["dishonesty"], "be-seen", ["cafe"], ["bar"]);
        var second = first with { Need = "slow-down" };

        Assert.Null(await story.GetProfileAsync(mira));
        Assert.Equal("be-seen", (await story.SetProfileAsync(mira, first)).Need);
        Assert.Equal("be-seen", (await story.SetProfileAsync(mira, second)).Need);
        Assert.Equal(first.Desires, (await story.GetProfileAsync(mira))!.Desires);
    }

    [Fact]
    public async Task Schedules_and_promises_round_trip_and_a_promise_resolves_once()
    {
        using var db = new TempDatabase();
        var story = new StoryStateRepository(db.Database);
        var (save, mira) = await NewSaveAsync(db);
        var id = mira.ToString();

        var schedule = new CharacterSchedule(id, [new ScheduleEntry(TimeOfDay.Morning, "corner-cafe", [0, 1, 2, 3, 4])]);
        await story.SaveScheduleAsync(save, schedule);
        Assert.Equal("corner-cafe", (await story.GetSchedulesAsync(save))[id].Where(new ClockState(2, TimeOfDay.Morning)));

        var meet = new Promise("meet-park", id, PromiseKind.Meet, MadeDay: 2, DueDay: 3, TimeOfDay.Evening, "riverside-park");
        await story.AddPromiseAsync(save, meet);
        Assert.Equal([meet], await story.GetPromisesAsync(save));

        await story.ResolvePromiseAsync(save, meet.Id, PromiseStatus.Kept, 3);
        Assert.Empty(await story.GetPromisesAsync(save));
        Assert.Equal(PromiseStatus.Kept, (await story.GetPromisesAsync(save, openOnly: false)).Single().Status);

        await Assert.ThrowsAsync<InvalidOperationException>(() => story.ResolvePromiseAsync(save, meet.Id, PromiseStatus.Broken, 4));

        // A meeting has to say where.
        await Assert.ThrowsAsync<SqliteException>(() =>
            story.AddPromiseAsync(save, meet with { Id = "nowhere", PlaceId = null }));
    }

    [Fact]
    public async Task A_save_ends_once_and_a_partner_goes_with_together_only()
    {
        using var db = new TempDatabase();
        var story = new StoryStateRepository(db.Database);
        var (save, mira) = await NewSaveAsync(db);

        Assert.Null(await story.GetEndingAsync(save));

        await Assert.ThrowsAsync<SqliteException>(() =>
            story.SaveEndingAsync(save, new StoredEnding(EndingKind.Alone, mira, 28, "{}")));

        var ending = new StoredEnding(EndingKind.Together, mira, 28, """{"text":"Weeks later."}""");
        await story.SaveEndingAsync(save, ending);
        Assert.Equal(ending, await story.GetEndingAsync(save));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            story.SaveEndingAsync(save, new StoredEnding(EndingKind.Alone, null, 28, "{}")));
    }

    [Fact]
    public async Task Turns_are_logged_in_order()
    {
        using var db = new TempDatabase();
        var story = new StoryStateRepository(db.Database);
        var (save, _) = await NewSaveAsync(db);

        await story.LogTurnAsync(save, new ClockState(1, TimeOfDay.Morning), "turn", new { place = "corner-cafe" });
        await story.LogTurnAsync(save, new ClockState(1, TimeOfDay.Midday), "choice", new { choice = "swap-numbers" });

        Assert.Equal("turn,choice", db.Scalar<string>("SELECT group_concat(kind) FROM (SELECT kind FROM turn_log ORDER BY id);"));
    }
}
