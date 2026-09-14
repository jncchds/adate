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

    // -------------------------------------------------------------------- cast

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
