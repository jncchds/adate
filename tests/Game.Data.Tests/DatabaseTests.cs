using Game.Core;
using Game.Core.Cast;
using Game.Core.Characters;
using Game.Core.Places;
using Game.Core.Scenes;
using Game.Data.Repositories;
using Microsoft.Data.Sqlite;

namespace Game.Data.Tests;

public class DatabaseTests
{
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

    [Fact]
    public void Migration_creates_every_table()
    {
        using var db = new TempDatabase();

        foreach (var table in new[]
                 {
                     "save", "character", "sprite_cache", "background_cache",
                     "place", "character_outfit", "game_clock", "flag", "visit",
                     "fact", "fact_knowledge", "rel_state", "schedule", "promise", "turn_log",
                     "player_profile", "memory",
                 })
        {
            var count = db.Scalar<long>(
                $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}';");

            Assert.Equal(1, count);
        }
    }

    [Fact]
    public void Migration_is_idempotent()
    {
        using var db = new TempDatabase();

        db.Database.Migrate();
        var applied = db.Scalar<long>("SELECT COUNT(*) FROM schema_migration;");

        db.Database.Migrate();

        // Counted against the first run rather than a literal, so adding a migration does
        // not fail a test that is about running twice, not about how many there are.
        Assert.True(applied > 0, "no migrations were applied at all");
        Assert.Equal(applied, db.Scalar<long>("SELECT COUNT(*) FROM schema_migration;"));
    }

    /// <summary>
    /// WAL is what lets the scene viewer read while a generation writes instead of blocking
    /// on it, and it has to be established on the connection rather than assumed.
    /// </summary>
    [Fact]
    public void Connections_run_in_wal_mode()
    {
        using var db = new TempDatabase();

        Assert.Equal("wal", db.Scalar<string>("PRAGMA journal_mode;"), ignoreCase: true);
    }

    /// <summary>
    /// SQLite disables foreign keys per connection by default, so the schema's ON DELETE
    /// CASCADE rules do nothing unless the pragma is set every time.
    /// </summary>
    [Fact]
    public void Foreign_keys_are_enforced()
    {
        using var db = new TempDatabase();
        using var connection = db.Database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO character (id, save_id, age, appearance_json)
            VALUES ('c1', 'no-such-save', 24, '{}');
            """;

        var ex = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        Assert.Contains("FOREIGN KEY", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>HANDOFF 1.9, enforced in the schema as well as in code.</summary>
    [Fact]
    public async Task Characters_below_the_absolute_floor_are_rejected()
    {
        using var db = new TempDatabase();
        var saves = new SaveRepository(db.Database);
        var characters = new CharacterRepository(db.Database);

        var save = await saves.CreateAsync("counterfeit-anime", "fingerprint", Ceiling.PG13);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            characters.CreateAsync(save.Id, Appearance() with { Age = 15 }));
    }

    /// <summary>
    /// The database half of the age clamp. <c>ContentPolicy</c> is the only route to a
    /// ceiling in code, but a cache row outlives the process that wrote it, so the schema
    /// refuses the row independently of whatever the caller believed.
    /// </summary>
    [Fact]
    public async Task A_sprite_above_pg13_cannot_be_stored_for_a_minor()
    {
        using var db = new TempDatabase();
        var saves = new SaveRepository(db.Database);
        var characters = new CharacterRepository(db.Database);
        var cache = new ImageCacheRepository(db.Database);

        var save = await saves.CreateAsync("counterfeit-anime", "fingerprint", Ceiling.Explicit);
        var character = await characters.CreateAsync(save.Id, Appearance() with { Age = 17 });

        await Assert.ThrowsAsync<SqliteException>(() => cache.RecordSpriteAsync(
            new string('a', 64), save.Id, character.Id,
            "outfit", "standing", "neutral", Ceiling.Explicit, "img/a.png"));
    }

    [Fact]
    public async Task A_minor_may_still_have_pg13_art()
    {
        using var db = new TempDatabase();
        var saves = new SaveRepository(db.Database);
        var characters = new CharacterRepository(db.Database);
        var cache = new ImageCacheRepository(db.Database);

        var save = await saves.CreateAsync("counterfeit-anime", "fingerprint", Ceiling.PG13);
        var character = await characters.CreateAsync(save.Id, Appearance() with { Age = 16 });

        await cache.RecordSpriteAsync(
            new string('b', 64), save.Id, character.Id,
            "outfit", "standing", "neutral", Ceiling.PG13, "img/b.png");
    }

    [Fact]
    public async Task A_character_round_trips_with_its_anchor()
    {
        using var db = new TempDatabase();
        var saves = new SaveRepository(db.Database);
        var characters = new CharacterRepository(db.Database);

        var save = await saves.CreateAsync("counterfeit-anime", "fingerprint", Ceiling.PG13);
        var created = await characters.CreateAsync(save.Id, Appearance());

        Assert.Null(created.AnchorImageHash);

        await characters.SetAnchorAsync(created.Id, new string('a', 64), anchorSeed: 4242);

        var loaded = await characters.GetAsync(created.Id);

        Assert.NotNull(loaded);
        Assert.Equal(new string('a', 64), loaded!.AnchorImageHash);
        Assert.Equal(4242, loaded.AnchorSeed);
        Assert.Equal("red hair", loaded.Appearance.HairColor);
        Assert.Equal(24, loaded.Age);
        Assert.Equal(save.Id, loaded.SaveId);
    }

    /// <summary>
    /// A candidate may move a feature to a nearby choice. Sprites are compiled from the stored
    /// record, so the approved look has to replace the declared one or they would not match it.
    /// </summary>
    [Fact]
    public async Task Approving_a_variant_replaces_the_stored_appearance()
    {
        using var db = new TempDatabase();
        var saves = new SaveRepository(db.Database);
        var characters = new CharacterRepository(db.Database);

        var save = await saves.CreateAsync("illustrious-anime", "fingerprint", Ceiling.PG13);
        var created = await characters.CreateAsync(save.Id, Appearance());

        await characters.SetAnchorAsync(
            created.Id, new string('a', 64), anchorSeed: 4242, Appearance() with { HairColor = "orange hair" });

        var loaded = await characters.GetAsync(created.Id);

        Assert.NotNull(loaded);
        Assert.Equal("orange hair", loaded!.Appearance.HairColor);
        Assert.Equal("long hair", loaded.Appearance.HairStyle);
        Assert.Equal(new string('a', 64), loaded.AnchorImageHash);
    }

    /// <summary>
    /// Age is what the content clamp is computed from, and subject selects the anchor tokens.
    /// Neither is something choosing a look may change, and a refused approval writes nothing.
    /// </summary>
    [Fact]
    public async Task An_approved_variant_cannot_change_age_or_subject()
    {
        using var db = new TempDatabase();
        var saves = new SaveRepository(db.Database);
        var characters = new CharacterRepository(db.Database);

        var save = await saves.CreateAsync("illustrious-anime", "fingerprint", Ceiling.PG13);
        var created = await characters.CreateAsync(save.Id, Appearance());

        await Assert.ThrowsAsync<InvalidOperationException>(() => characters.SetAnchorAsync(
            created.Id, new string('a', 64), anchorSeed: 1, Appearance() with { Age = 30 }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => characters.SetAnchorAsync(
            created.Id, new string('a', 64), anchorSeed: 1, Appearance() with { Subject = "male" }));

        var loaded = await characters.GetAsync(created.Id);

        Assert.Null(loaded!.AnchorImageHash);
        Assert.Equal(Appearance(), loaded.Appearance);
    }

    /// <summary>
    /// HANDOFF 1.8. The failure this guards against is silent: a sprite generated under one
    /// ceiling being served into a session running at another.
    /// </summary>
    [Fact]
    public async Task Sprite_lookup_is_scoped_by_ceiling()
    {
        using var db = new TempDatabase();
        var saves = new SaveRepository(db.Database);
        var characters = new CharacterRepository(db.Database);
        var cache = new ImageCacheRepository(db.Database);

        var save = await saves.CreateAsync("counterfeit-anime", "fingerprint", Ceiling.PG13);
        var character = await characters.CreateAsync(save.Id, Appearance());

        await cache.RecordSpriteAsync(
            new string('b', 64), save.Id, character.Id,
            "default", "standing", "smile", Ceiling.Suggestive, "img/b.png");

        Assert.Null(await cache.FindSpritePathAsync(
            save.Id, character.Id, "default", "standing", "smile", Ceiling.PG13));

        Assert.Equal("img/b.png", await cache.FindSpritePathAsync(
            save.Id, character.Id, "default", "standing", "smile", Ceiling.Suggestive));
    }

    /// <summary>
    /// HANDOFF 1.4. Two saves holding the same character look must not see each other's art.
    /// </summary>
    [Fact]
    public async Task Sprite_lookup_is_scoped_by_save()
    {
        using var db = new TempDatabase();
        var saves = new SaveRepository(db.Database);
        var characters = new CharacterRepository(db.Database);
        var cache = new ImageCacheRepository(db.Database);

        var first = await saves.CreateAsync("counterfeit-anime", "fingerprint", Ceiling.PG13);
        var second = await saves.CreateAsync("counterfeit-anime", "fingerprint", Ceiling.PG13);
        var character = await characters.CreateAsync(first.Id, Appearance());

        await cache.RecordSpriteAsync(
            new string('c', 64), first.Id, character.Id,
            "default", "standing", "smile", Ceiling.PG13, "img/c.png");

        Assert.Null(await cache.FindSpritePathAsync(
            second.Id, character.Id, "default", "standing", "smile", Ceiling.PG13));
    }

    [Fact]
    public async Task Recording_the_same_sprite_hash_twice_is_a_no_op()
    {
        using var db = new TempDatabase();
        var saves = new SaveRepository(db.Database);
        var characters = new CharacterRepository(db.Database);
        var cache = new ImageCacheRepository(db.Database);

        var save = await saves.CreateAsync("counterfeit-anime", "fingerprint", Ceiling.PG13);
        var character = await characters.CreateAsync(save.Id, Appearance());
        var hash = new string('d', 64);

        for (var i = 0; i < 3; i++)
        {
            await cache.RecordSpriteAsync(
                hash, save.Id, character.Id,
                "default", "standing", "smile", Ceiling.PG13, "img/d.png");
        }

        Assert.Equal(1, db.Scalar<long>("SELECT COUNT(*) FROM sprite_cache;"));
    }

    [Fact]
    public async Task Expressions_for_one_look_come_back_together()
    {
        using var db = new TempDatabase();
        var saves = new SaveRepository(db.Database);
        var characters = new CharacterRepository(db.Database);
        var cache = new ImageCacheRepository(db.Database);

        var save = await saves.CreateAsync("counterfeit-anime", "fingerprint", Ceiling.PG13);
        var character = await characters.CreateAsync(save.Id, Appearance());

        var expressions = new[] { "smile", "sad", "angry", "surprised", "neutral", "blush" };
        for (var i = 0; i < expressions.Length; i++)
        {
            await cache.RecordSpriteAsync(
                new string((char)('a' + i), 64), save.Id, character.Id,
                "default", "standing", expressions[i], Ceiling.PG13, $"img/{expressions[i]}.png");
        }

        var found = await cache.ListExpressionsAsync(
            save.Id, character.Id, "default", "standing", Ceiling.PG13);

        Assert.Equal(6, found.Count);
        Assert.Equal("img/smile.png", found["smile"]);
    }

    [Fact]
    public async Task Backgrounds_are_keyed_by_location_and_time()
    {
        using var db = new TempDatabase();
        var saves = new SaveRepository(db.Database);
        var cache = new ImageCacheRepository(db.Database);

        var save = await saves.CreateAsync("counterfeit-anime", "fingerprint", Ceiling.PG13);

        await cache.RecordBackgroundAsync(
            new string('e', 64), save.Id, "cafe", TimeOfDay.Evening, "img/e.png");

        Assert.Equal("img/e.png",
            await cache.FindBackgroundPathAsync(save.Id, "cafe", TimeOfDay.Evening));

        Assert.Null(await cache.FindBackgroundPathAsync(save.Id, "cafe", TimeOfDay.Morning));
        Assert.Null(await cache.FindBackgroundPathAsync(save.Id, "park", TimeOfDay.Evening));
    }

    // ------------------------------------------------------------------ places

    private static PlaceRecord Place(Game.Core.Saves.SaveId save, string id, bool known = true) =>
        new(save, id, "cafe", id, ["window-seats"], PlaceRecord.SeedFor(save, id), PlaceOrigin.Authored, known, known ? 1 : null);

    [Fact]
    public async Task Places_are_scoped_by_save_and_adding_them_twice_changes_nothing()
    {
        using var db = new TempDatabase();
        var saves = new SaveRepository(db.Database);
        var places = new PlaceRepository(db.Database);

        var first = await saves.CreateAsync("zimage-anime", "fingerprint", Ceiling.PG13);
        var second = await saves.CreateAsync("zimage-anime", "fingerprint", Ceiling.PG13);

        PlaceRecord[] authored = [Place(first.Id, "corner-cafe"), Place(first.Id, "low-tide", known: false)];
        await places.AddAsync(authored);
        await places.AddAsync(authored);

        var all = await places.ListAsync(first.Id);
        Assert.Equal(["corner-cafe", "low-tide"], all.Select(p => p.Id));
        Assert.Equal(authored[0], all[0] with { Details = authored[0].Details });
        Assert.Equal(["window-seats"], all[0].Details);

        Assert.Equal(["corner-cafe"], (await places.ListAsync(first.Id, knownOnly: true)).Select(p => p.Id));
        Assert.Empty(await places.ListAsync(second.Id));
    }

    [Fact]
    public async Task A_place_becomes_known_and_keeps_its_first_day()
    {
        using var db = new TempDatabase();
        var saves = new SaveRepository(db.Database);
        var places = new PlaceRepository(db.Database);

        var save = await saves.CreateAsync("zimage-anime", "fingerprint", Ceiling.PG13);
        await places.AddAsync([Place(save.Id, "low-tide", known: false)]);

        await places.MarkKnownAsync(save.Id, "low-tide", day: 6);
        await places.MarkKnownAsync(save.Id, "low-tide", day: 9);

        var place = await places.GetAsync(save.Id, "low-tide");
        Assert.True(place!.Known);
        Assert.Equal(6, place.FirstDay);
    }

    /// <summary>A setting is locked like a style pack: the save's rows refer to its places and openings.</summary>
    [Fact]
    public async Task A_saves_setting_is_set_once()
    {
        using var db = new TempDatabase();
        var saves = new SaveRepository(db.Database);

        var save = await saves.CreateAsync("zimage-anime", "fingerprint", Ceiling.PG13);

        Assert.Null(await saves.GetSettingIdAsync(save.Id));
        Assert.Equal("big-city", await saves.SetSettingAsync(save.Id, "big-city"));
        Assert.Equal("big-city", await saves.SetSettingAsync(save.Id, "summer-camp"));
    }

    // ------------------------------------------------------------------ turns

    [Fact]
    public async Task A_new_game_starts_on_day_one_morning_once()
    {
        using var db = new TempDatabase();
        var saves = new SaveRepository(db.Database);
        var state = new GameStateRepository(db.Database);

        var save = await saves.CreateAsync("zimage-anime", "fingerprint", Ceiling.PG13);

        Assert.Equal(Game.Core.World.ClockState.Start, await state.GetOrStartClockAsync(save.Id));
        Assert.Equal(Game.Core.World.ClockState.Start, await state.GetOrStartClockAsync(save.Id));
        Assert.Equal(1, db.Scalar<long>("SELECT COUNT(*) FROM game_clock;"));
    }

    private static Game.Core.Encounters.TurnOutcome Turn(
        Game.Core.World.ClockState at,
        string place,
        IReadOnlyList<string>? with = null,
        IReadOnlyList<string>? reveals = null) => new(
        at,
        at.Next(),
        place,
        "tip",
        "A tip.",
        new Dictionary<string, string> { ["knows.bar"] = "true", ["encounter.tip"] = at.Day.ToString() },
        reveals ?? [],
        with ?? [],
        GameOver: false);

    [Fact]
    public async Task A_turn_moves_the_clock_records_the_visit_sets_flags_and_reveals_places()
    {
        using var db = new TempDatabase();
        var saves = new SaveRepository(db.Database);
        var places = new PlaceRepository(db.Database);
        var state = new GameStateRepository(db.Database);

        var save = await saves.CreateAsync("zimage-anime", "fingerprint", Ceiling.PG13);
        await places.AddAsync([Place(save.Id, "corner-cafe"), Place(save.Id, "low-tide", known: false)]);
        var start = await state.GetOrStartClockAsync(save.Id);

        await state.CommitTurnAsync(save.Id, Turn(start, "corner-cafe", reveals: ["low-tide"]));

        Assert.Equal(start.Next(), await state.GetOrStartClockAsync(save.Id));
        Assert.Equal("true", (await state.GetFlagsAsync(save.Id))["knows.bar"]);
        Assert.True((await places.GetAsync(save.Id, "low-tide"))!.Known);
        Assert.Equal(1, (await places.GetAsync(save.Id, "low-tide"))!.FirstDay);
        Assert.Equal(1, await state.CountAloneVisitsAsync(save.Id, "corner-cafe"));
    }

    /// <summary>A double click, or two tabs, plans the same turn twice. It is applied once.</summary>
    [Fact]
    public async Task A_turn_planned_from_a_clock_that_has_moved_on_is_refused_and_changes_nothing()
    {
        using var db = new TempDatabase();
        var saves = new SaveRepository(db.Database);
        var places = new PlaceRepository(db.Database);
        var state = new GameStateRepository(db.Database);

        var save = await saves.CreateAsync("zimage-anime", "fingerprint", Ceiling.PG13);
        await places.AddAsync([Place(save.Id, "corner-cafe")]);
        var start = await state.GetOrStartClockAsync(save.Id);

        await state.CommitTurnAsync(save.Id, Turn(start, "corner-cafe"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => state.CommitTurnAsync(save.Id, Turn(start, "corner-cafe")));

        Assert.Equal(1, db.Scalar<long>("SELECT COUNT(*) FROM visit;"));
        Assert.Equal(start.Next(), await state.GetOrStartClockAsync(save.Id));
    }

    [Fact]
    public async Task A_visit_with_company_does_not_count_as_a_solo_visit()
    {
        using var db = new TempDatabase();
        var saves = new SaveRepository(db.Database);
        var places = new PlaceRepository(db.Database);
        var state = new GameStateRepository(db.Database);

        var save = await saves.CreateAsync("zimage-anime", "fingerprint", Ceiling.PG13);
        await places.AddAsync([Place(save.Id, "low-tide")]);
        var start = await state.GetOrStartClockAsync(save.Id);

        await state.CommitTurnAsync(save.Id, Turn(start, "low-tide", with: ["main_li"]));

        Assert.Equal(0, await state.CountAloneVisitsAsync(save.Id, "low-tide"));
    }

    [Fact]
    public async Task Choosing_an_opening_starts_the_clock_sets_flags_and_reveals_places_once()
    {
        using var db = new TempDatabase();
        var saves = new SaveRepository(db.Database);
        var places = new PlaceRepository(db.Database);
        var state = new GameStateRepository(db.Database);

        var save = await saves.CreateAsync("zimage-anime", "fingerprint", Ceiling.PG13, "big-city", "Alex", "woman");
        await places.AddAsync([Place(save.Id, "riverside-park"), Place(save.Id, "low-tide", known: false)]);

        var start = new Game.Core.World.ClockState(1, Game.Core.Scenes.TimeOfDay.Evening);
        await state.StartOpeningAsync(save.Id, "lost-and-found", "riverside-park", start, ["riverside-park", "low-tide"]);

        Assert.Equal(start, await state.GetOrStartClockAsync(save.Id));
        var flags = await state.GetFlagsAsync(save.Id);
        Assert.Equal("lost-and-found", flags["opening"]);
        Assert.Equal("riverside-park", flags["main_li.home_place"]);
        Assert.True((await places.GetAsync(save.Id, "low-tide"))!.Known);
        Assert.Equal("Alex", await saves.GetPlayerNameAsync(save.Id));
        Assert.Equal("big-city", await saves.GetSettingIdAsync(save.Id));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            state.StartOpeningAsync(save.Id, "shared-table", "corner-cafe", Game.Core.World.ClockState.Start, []));
    }

    [Fact]
    public async Task A_choice_is_answered_once_and_only_when_it_is_open()
    {
        using var db = new TempDatabase();
        var saves = new SaveRepository(db.Database);
        var places = new PlaceRepository(db.Database);
        var state = new GameStateRepository(db.Database);

        var save = await saves.CreateAsync("zimage-anime", "fingerprint", Ceiling.PG13);
        await places.AddAsync([Place(save.Id, "corner-cafe")]);
        var start = await state.GetOrStartClockAsync(save.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            state.ResolveChoiceAsync(save.Id, "opening.shared-table.recognise", "swap-numbers", new Dictionary<string, string>()));

        var turn = Turn(start, "corner-cafe") with
        {
            FlagsToSet = new Dictionary<string, string> { ["pending.choice"] = "opening.shared-table.recognise" },
        };
        await state.CommitTurnAsync(save.Id, turn);

        await state.ResolveChoiceAsync(save.Id, "opening.shared-table.recognise", "swap-numbers",
            new Dictionary<string, string> { ["main_li.contact"] = "true" });

        var flags = await state.GetFlagsAsync(save.Id);
        Assert.Equal("false", flags["pending.choice"]);
        Assert.Equal("true", flags["main_li.contact"]);
        Assert.Equal("swap-numbers", flags["choice.opening.shared-table.recognise"]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            state.ResolveChoiceAsync(save.Id, "opening.shared-table.recognise", "let-it-go", new Dictionary<string, string>()));
    }

    [Fact]
    public async Task A_main_LI_is_stored_with_their_name_and_chosen_temper()
    {
        using var db = new TempDatabase();
        var saves = new SaveRepository(db.Database);
        var characters = new CharacterRepository(db.Database);

        var save = await saves.CreateAsync("zimage-anime", "fingerprint", Ceiling.PG13);
        var temper = new Dictionary<string, string> { ["temper"] = "fiery", ["energy"] = "reserved" };
        var main = await characters.CreateAsync(save.Id, Appearance(), "Sam", temper);

        Assert.Equal(main.Id, (await characters.GetMainAsync(save.Id))!.Id);
        Assert.Equal("Sam", await characters.GetNameAsync(main.Id));
        Assert.Equal(temper, await characters.GetTemperAsync(main.Id));

        // A temper chosen at creation is not a stored cast: the cast is built later, on approval.
        Assert.Null(await characters.GetCastAsync(main.Id));

        // Storing the cast keeps the chosen temper and fills in want and aesthetic.
        await characters.SetAnchorAsync(main.Id, new string('a', 64), anchorSeed: 1);
        var anchored = (await characters.GetAsync(main.Id))!;
        await characters.SaveCastAsync(anchored, Lead(anchored.Appearance), [Variant(anchored.Appearance)]);

        var cast = await characters.GetCastAsync(main.Id);
        Assert.Equal("fiery", cast![0].Temper["temper"]);
        Assert.Equal("open-a-bakery", cast[0].WantId);
    }

    [Fact]
    public async Task Last_seen_is_the_latest_day_each_person_shared_a_scene()
    {
        using var db = new TempDatabase();
        var saves = new SaveRepository(db.Database);
        var places = new PlaceRepository(db.Database);
        var state = new GameStateRepository(db.Database);

        var save = await saves.CreateAsync("zimage-anime", "fingerprint", Ceiling.PG13);
        await places.AddAsync([Place(save.Id, "corner-cafe")]);

        var clock = await state.GetOrStartClockAsync(save.Id);
        await state.CommitTurnAsync(save.Id, Turn(clock, "corner-cafe") with { With = ["main_li", "variant:chance"] });
        clock = clock.Next();
        await state.CommitTurnAsync(save.Id, Turn(clock, "corner-cafe") with { With = [] });

        for (var i = 0; i < 5; i++)
        {
            clock = clock.Next();
            await state.CommitTurnAsync(save.Id, Turn(clock, "corner-cafe") with { With = [] });
        }

        clock = clock.Next();
        await state.CommitTurnAsync(save.Id, Turn(clock, "corner-cafe") with { With = ["main_li"] });

        var seen = await state.GetLastSeenAsync(save.Id);
        Assert.Equal(clock.Day, seen["main_li"]);
        Assert.Equal(1, seen["variant:chance"]);
        Assert.Equal(2, seen.Count);
    }

    [Fact]
    public async Task Relationship_changes_commit_with_the_turn_or_not_at_all()
    {
        using var db = new TempDatabase();
        var saves = new SaveRepository(db.Database);
        var places = new PlaceRepository(db.Database);
        var characters = new CharacterRepository(db.Database);
        var state = new GameStateRepository(db.Database);
        var story = new StoryStateRepository(db.Database);

        var save = await saves.CreateAsync("zimage-anime", "fingerprint", Ceiling.PG13);
        var main = await characters.CreateAsync(save.Id, Appearance(), "Sam");
        await places.AddAsync([Place(save.Id, "corner-cafe")]);
        var start = await state.GetOrStartClockAsync(save.Id);

        var met = Game.Core.Story.RelationshipState.Start with { Affection = 5, Stage = Game.Core.Story.RelationshipStage.Acquaintance };
        await state.CommitTurnAsync(save.Id, Turn(start, "corner-cafe"), new Dictionary<Guid, Game.Core.Story.RelationshipState> { [main.Id] = met });
        Assert.Equal(met, await story.GetRelationshipAsync(save.Id, main.Id));

        // The same turn again is refused by the clock guard, and its relationship change is not written either.
        await Assert.ThrowsAsync<InvalidOperationException>(() => state.CommitTurnAsync(
            save.Id, Turn(start, "corner-cafe"), new Dictionary<Guid, Game.Core.Story.RelationshipState> { [main.Id] = met with { Affection = 50 } }));
        Assert.Equal(5, (await story.GetRelationshipAsync(save.Id, main.Id)).Affection);
    }

    // -------------------------------------------------------------------- cast

    [Fact]
    public async Task Cast_identities_follow_the_cast_order_and_are_given_once()
    {
        using var db = new TempDatabase();
        var saves = new SaveRepository(db.Database);
        var characters = new CharacterRepository(db.Database);

        var save = await saves.CreateAsync("zimage-anime", "fingerprint", Ceiling.PG13);
        var created = await characters.CreateAsync(save.Id, Appearance(), "Sam");
        await characters.SetAnchorAsync(created.Id, new string('a', 64), anchorSeed: 1);
        var main = (await characters.GetAsync(created.Id))!;

        await characters.SaveCastAsync(main, Lead(main.Appearance),
        [
            Variant(main.Appearance with { HairColor = "purple hair" }),
            Variant(main.Appearance with { Age = 30 }, "other-life"),
        ]);

        var identities = await characters.GetCastIdentitiesAsync(main.Id);
        Assert.Equal(main.Id, identities[0].Id);
        Assert.Equal("Sam", identities[0].Name);
        Assert.Equal([null, "bolder", "other-life"], identities.Select(i => i.ProfileId));
        Assert.All(identities.Skip(1), i => Assert.Null(i.Route));

        await characters.SetIdentityAsync(identities[1].Id, "Kai", "chance");
        await characters.SetIdentityAsync(identities[1].Id, "Ren", "routine");

        var again = await characters.GetCastIdentitiesAsync(main.Id);
        Assert.Equal(("Kai", "chance"), (again[1].Name, again[1].Route));

        // The main LI is named by the player and has no route.
        await Assert.ThrowsAsync<InvalidOperationException>(() => characters.SetIdentityAsync(main.Id, "Other", "routine"));
    }

    private static CastMember Lead(CharacterAppearance appearance) => new(
        null,
        appearance,
        "artsy",
        new Dictionary<string, string> { ["temper"] = "calm", ["energy"] = "reserved" },
        "open-a-bakery",
        4242,
        []);

    private static CastMember Variant(CharacterAppearance appearance, string profile = "bolder") => new(
        profile,
        appearance,
        "sporty",
        new Dictionary<string, string> { ["temper"] = "fiery", ["energy"] = "outgoing" },
        "finish-a-novel",
        777,
        [new CastChange("hairColor", "red hair", "purple hair")]);

    private static async Task<(CharacterRepository Characters, CharacterRecord Main)> MainWithAnchor(TempDatabase db, int age = 24)
    {
        var saves = new SaveRepository(db.Database);
        var characters = new CharacterRepository(db.Database);

        var save = await saves.CreateAsync("zimage-anime", "fingerprint", Ceiling.PG13);
        var main = await characters.CreateAsync(save.Id, Appearance() with { Age = age });
        await characters.SetAnchorAsync(main.Id, new string('a', 64), anchorSeed: 4242);

        return (characters, (await characters.GetAsync(main.Id))!);
    }

    [Fact]
    public async Task A_cast_round_trips_main_first_and_is_stored_only_once()
    {
        using var db = new TempDatabase();
        var (characters, main) = await MainWithAnchor(db);

        Assert.Null(await characters.GetCastAsync(main.Id));

        await characters.SaveCastAsync(main, Lead(main.Appearance),
        [
            Variant(main.Appearance with { HairColor = "purple hair" }),
            Variant(main.Appearance with { Age = 30 }, "other-life"),
        ]);

        // A second build must not add to or replace the stored cast.
        await characters.SaveCastAsync(main, Lead(main.Appearance) with { WantId = "leave-town" },
            [Variant(main.Appearance with { HairColor = "blue hair" })]);

        var cast = await characters.GetCastAsync(main.Id);

        Assert.NotNull(cast);
        Assert.Equal(3, cast!.Count);
        Assert.True(cast[0].IsMain);
        Assert.Equal("open-a-bakery", cast[0].WantId);
        Assert.Equal("reserved", cast[0].Temper["energy"]);
        Assert.Equal("bolder", cast[1].ProfileId);
        Assert.Equal("purple hair", cast[1].Appearance.HairColor);
        Assert.Equal(777, cast[1].Seed);
        Assert.Equal(new CastChange("hairColor", "red hair", "purple hair"), Assert.Single(cast[1].LookChanges));
        Assert.Equal(30, cast[2].Appearance.Age);
        Assert.Equal(2, db.Scalar<long>($"SELECT COUNT(*) FROM character WHERE variant_of = '{main.Id}';"));
    }

    [Fact]
    public async Task A_variant_with_a_different_subject_is_refused_by_the_schema()
    {
        using var db = new TempDatabase();
        var (characters, main) = await MainWithAnchor(db);

        var ex = await Assert.ThrowsAsync<SqliteException>(() => characters.SaveCastAsync(
            main, Lead(main.Appearance), [Variant(main.Appearance with { Subject = "male" })]));

        Assert.Contains("subject", ex.Message, StringComparison.Ordinal);
        Assert.Null(await characters.GetCastAsync(main.Id));
    }

    [Fact]
    public async Task A_variant_of_an_adult_cannot_be_a_minor()
    {
        using var db = new TempDatabase();
        var (characters, main) = await MainWithAnchor(db);

        var ex = await Assert.ThrowsAsync<SqliteException>(() => characters.SaveCastAsync(
            main, Lead(main.Appearance), [Variant(main.Appearance with { Age = 17 })]));

        Assert.Contains("must be an adult", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_variant_of_a_main_LI_under_18_must_share_their_exact_age()
    {
        using var db = new TempDatabase();
        var (characters, main) = await MainWithAnchor(db, age: 17);

        var ex = await Assert.ThrowsAsync<SqliteException>(() => characters.SaveCastAsync(
            main, Lead(main.Appearance), [Variant(main.Appearance with { Age = 18 })]));
        Assert.Contains("exact age", ex.Message, StringComparison.Ordinal);

        await characters.SaveCastAsync(main, Lead(main.Appearance), [Variant(main.Appearance)]);
        Assert.Equal(2, (await characters.GetCastAsync(main.Id))!.Count);
    }

    /// <summary>Every variant was checked against the main LI as they were; the main LI cannot move afterwards.</summary>
    [Fact]
    public async Task A_main_LI_with_a_stored_cast_cannot_change_age()
    {
        using var db = new TempDatabase();
        var (characters, main) = await MainWithAnchor(db);

        await characters.SaveCastAsync(main, Lead(main.Appearance), [Variant(main.Appearance)]);

        using var connection = db.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE character SET age = 40 WHERE id = '{main.Id}';";

        var ex = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        Assert.Contains("cannot change age or subject", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_saves_pack_fingerprint_is_recoverable()
    {
        using var db = new TempDatabase();
        var saves = new SaveRepository(db.Database);

        var save = await saves.CreateAsync("counterfeit-anime", "abc123", Ceiling.PG13);

        Assert.Equal("abc123", await saves.GetPackFingerprintAsync(save.Id));
        Assert.Equal("counterfeit-anime", (await saves.GetAsync(save.Id))!.StylePackId);
    }
}
