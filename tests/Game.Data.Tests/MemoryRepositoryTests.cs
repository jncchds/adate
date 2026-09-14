using Game.Core;
using Game.Core.Story;
using Game.Data.Repositories;
using Microsoft.Data.Sqlite;

namespace Game.Data.Tests;

public class MemoryRepositoryTests
{
    private static MemoryEntry Memory(int day, string summary, float[]? embedding = null, string[]? tags = null, MemoryScope scope = MemoryScope.Scene) =>
        new(0, scope, day, summary, ["rin"], tags ?? [], embedding);

    [Fact]
    public async Task Memories_round_trip_with_their_embeddings()
    {
        using var db = new TempDatabase();
        var save = await new SaveRepository(db.Database).CreateAsync("zimage-anime", "fingerprint", Ceiling.PG13);
        var memories = new MemoryRepository(db.Database);

        await memories.AddAsync(save.Id, Memory(3, "Rin laughed at the flyer.", [0.25f, -1.5f, 3f], [MemoryTags.First]));
        await memories.AddAsync(save.Id, Memory(4, "Coffee, again."));

        var stored = await memories.ListAsync(save.Id);

        Assert.Equal(2, stored.Count);
        Assert.Equal([0.25f, -1.5f, 3f], stored[0].Embedding!);
        Assert.Equal([MemoryTags.First], stored[0].Tags);
        Assert.Equal(["rin"], stored[0].People);
        Assert.Null(stored[1].Embedding);
    }

    [Fact]
    public async Task Compaction_folds_members_once_and_never_touches_firsts_or_conflicts()
    {
        using var db = new TempDatabase();
        var save = await new SaveRepository(db.Database).CreateAsync("zimage-anime", "fingerprint", Ceiling.PG13);
        var memories = new MemoryRepository(db.Database);

        var a = await memories.AddAsync(save.Id, Memory(3, "Coffee."));
        var b = await memories.AddAsync(save.Id, Memory(3, "A walk."));
        var first = await memories.AddAsync(save.Id, Memory(3, "The first date.", tags: [MemoryTags.First]));

        var day = await memories.CompactAsync(save.Id, Memory(3, "Coffee and a walk.", scope: MemoryScope.Day), [a, b]);

        var stored = await memories.ListAsync(save.Id);
        Assert.Equal(day, stored.Single(m => m.Id == a).CompactedInto);
        Assert.Equal(day, stored.Single(m => m.Id == b).CompactedInto);

        await Assert.ThrowsAsync<SqliteException>(() =>
            memories.CompactAsync(save.Id, Memory(3, "Again.", scope: MemoryScope.Day), [a]));

        await Assert.ThrowsAsync<SqliteException>(() =>
            memories.CompactAsync(save.Id, Memory(3, "Nope.", scope: MemoryScope.Day), [first]));

        // Neither refused compaction left its summary behind.
        Assert.Equal(4, (await memories.ListAsync(save.Id)).Count);
    }
}
