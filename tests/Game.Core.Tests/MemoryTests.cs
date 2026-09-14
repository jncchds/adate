using Game.Core.Story;

namespace Game.Core.Tests;

public class MemoryTests
{
    private static MemoryEntry Scene(long id, int day, string[] people, float[]? embedding = null, string[]? tags = null, MemoryScope scope = MemoryScope.Scene, long? compacted = null) =>
        new(id, scope, day, $"memory {id}", people, tags ?? [], embedding, compacted);

    [Fact]
    public void Cosine_is_one_for_the_same_direction_and_zero_for_unrelated_or_mismatched_vectors()
    {
        Assert.Equal(1, MemoryRetrieval.Cosine([1, 2, 3], [2, 4, 6]), 6);
        Assert.Equal(0, MemoryRetrieval.Cosine([1, 0], [0, 1]), 6);
        Assert.Equal(0, MemoryRetrieval.Cosine([1, 0], [1, 0, 0]));
        Assert.Equal(0, MemoryRetrieval.Cosine([0, 0], [1, 0]));
    }

    [Fact]
    public void Retrieval_prefers_similar_memories_with_someone_present_and_skips_compacted_ones()
    {
        MemoryEntry[] memories =
        [
            Scene(1, 2, ["rin"], [1, 0]),
            Scene(2, 5, ["rin"], [0, 1]),
            Scene(3, 5, ["june"], [1, 0]),
            Scene(4, 1, ["rin"], [1, 0], compacted: 9),
        ];

        var found = MemoryRetrieval.Retrieve(memories, [1, 0], ["rin"], today: 6, top: 2);

        Assert.Equal([1L, 2L], found.Select(m => m.Id));
    }

    [Fact]
    public void Without_embeddings_retrieval_falls_back_to_the_most_recent()
    {
        MemoryEntry[] memories = [Scene(1, 2, ["rin"]), Scene(2, 5, ["rin"]), Scene(3, 4, ["rin"])];

        Assert.Equal([2L, 3L], MemoryRetrieval.Retrieve(memories, null, ["rin"], today: 6, top: 2).Select(m => m.Id));
    }

    [Fact]
    public void The_last_shared_scene_is_the_latest_one_with_someone_present()
    {
        MemoryEntry[] memories = [Scene(1, 2, ["rin"]), Scene(2, 5, ["june"]), Scene(3, 4, ["rin", "june"])];

        Assert.Equal(3, MemoryRetrieval.LastShared(memories, ["rin"])!.Id);
        Assert.Null(MemoryRetrieval.LastShared(memories, ["kai"]));
    }

    [Fact]
    public void Earlier_days_compact_into_days_and_earlier_weeks_into_weeks_keeping_firsts_and_conflicts()
    {
        MemoryEntry[] memories =
        [
            Scene(1, 3, ["rin"]),
            Scene(2, 3, ["rin"]),
            Scene(3, 3, ["rin"], tags: [MemoryTags.First]),
            Scene(4, 9, ["rin"]),
            Scene(5, 9, ["rin"]),
            Scene(6, 4, ["june"]),
            Scene(10, 2, [], scope: MemoryScope.Day),
            Scene(11, 6, [], scope: MemoryScope.Day),
            Scene(12, 12, [], scope: MemoryScope.Day),
        ];

        var groups = MemoryRetrieval.CompactionGroups(memories, today: 9);

        var day = Assert.Single(groups, g => g.Into is MemoryScope.Day);
        Assert.Equal(3, day.Day);
        Assert.Equal([1L, 2L], day.Members.Select(m => m.Id));

        var week = Assert.Single(groups, g => g.Into is MemoryScope.Week);
        Assert.Equal([10L, 11L], week.Members.Select(m => m.Id));
        Assert.Equal(6, week.Day);
    }
}
