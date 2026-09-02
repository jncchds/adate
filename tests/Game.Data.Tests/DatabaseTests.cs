using Game.Core;
using Game.Core.Characters;
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

        foreach (var table in new[] { "save", "character", "sprite_cache", "background_cache" })
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
