using Game.Core.Story;

namespace Game.Core.Tests;

public class StoryThreadTests
{
    [Fact]
    public void A_scene_gets_the_loose_ends_about_the_people_there_first_then_the_players_own_and_never_stale_or_closed_ones()
    {
        StoryThread[] open =
        [
            new(1, "rin", "Rin promised a record.", 3, null),
            new(2, "kai", "Kai asked about the job.", 9, null),
            new(3, null, "The player meant to call home.", 8, null),
            new(4, "rin", "Rin mentioned a concert.", 9, null),
            new(5, "rin", "Rin lost a glove.", 9, 9),
            new(6, "rin", "Rin said she would learn to swim.", 1, null),
        ];

        var picked = StoryThreads.ForScene(open, ["rin"], day: 12);

        Assert.Equal([4L, 1L, 3L], picked.Select(t => t.Id));
    }

    [Fact]
    public void At_most_a_packets_worth_is_handed_over()
    {
        var open = Enumerable.Range(1, 12).Select(i => new StoryThread(i, "rin", $"Thread {i}.", 5, null));

        Assert.Equal(StoryThreads.PerPacket, StoryThreads.ForScene(open, ["rin"], 6).Count);
    }

    [Fact]
    public void New_loose_ends_are_trimmed_deduplicated_and_capped()
    {
        var kept = StoryThreads.Keep(["  Rin asked a question. ", "rin asked a question.", "", new string('x', StoryThreads.MaxLength + 1), "Kai owes a coffee.", "Third."]);

        Assert.Equal(["Rin asked a question.", "Kai owes a coffee."], kept);
        Assert.Empty(StoryThreads.Keep(null));
    }
}
