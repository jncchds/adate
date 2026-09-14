using Game.Core.Scenes;
using Game.Core.Story;
using Game.Core.World;

namespace Game.Core.Tests;

public class ScenePacketTests
{
    private static KnownFact Fact(long id, string subject, string value, params string[] knowers) =>
        new(id, new Fact(subject, "likes", value, FactLevel.Established, "scene", 1), knowers.ToHashSet());

    private static ScenePacket Packet(IReadOnlyList<KnownFact>? playerKnows = null, IReadOnlyList<KnownFact>? presentKnow = null) => new(
        "Big city",
        "Busy weekdays.",
        new ClockState(6, TimeOfDay.Evening),
        "corner-cafe",
        "The Corner Cup",
        "Alex",
        [new PacketPerson("rin-id", "Rin", ["Speaks evenly."], RelationshipStage.Friend, "keep the local animal shelter from closing")],
        playerKnows ?? [Fact(1, "rin-id", "jazz", FactLedger.Player)],
        presentKnow ?? [],
        "Rin admits what they really want.",
        Ceiling.PG13,
        ["neutral", "smile"]);

    [Fact]
    public void An_english_story_keeps_the_english_second_person_rule_and_nothing_more()
    {
        var text = ScenePacketBuilder.Render(Packet());

        Assert.Contains("you, your. Never I, me, my", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Write the prose in", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_story_in_another_language_asks_for_its_prose_there_and_keeps_ids_in_english()
    {
        var text = ScenePacketBuilder.Render(Packet() with { Language = "Русский", PlayerGender = "man" });

        Assert.Contains("- Write the prose in Русский", text, StringComparison.Ordinal);
        Assert.Contains("in English: JSON keys, ids, tags", text, StringComparison.Ordinal);
        Assert.Contains("masculine forms for the player", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Never I, me, my", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_packet_follows_the_plans_order_and_uses_names_not_ids()
    {
        var text = ScenePacketBuilder.Render(Packet(presentKnow: [Fact(2, "rin-id", "an old band", "rin-id")]));

        string[] sections = ["## Where and when", "## Who is here", "## What Alex knows", "## What the people here know", "## What must happen", "## Rules"];
        var positions = sections.Select(s => text.IndexOf(s, StringComparison.Ordinal)).ToList();

        Assert.All(positions, p => Assert.True(p >= 0));
        Assert.Equal(positions.Order(), positions);
        Assert.Contains("- Rin likes jazz", text, StringComparison.Ordinal);
        Assert.Contains("Day 6, evening, at The Corner Cup.", text, StringComparison.Ordinal);
        Assert.Contains("a friend", text, StringComparison.Ordinal);
        Assert.Contains("keep the local animal shelter from closing", text, StringComparison.Ordinal);
        Assert.Contains("PG-13", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Facts_are_dropped_oldest_first_until_the_packet_fits_the_budget()
    {
        var many = Enumerable.Range(1, 2000).Select(i => Fact(i, "rin-id", $"thing number {i}", FactLedger.Player)).ToList();

        var text = ScenePacketBuilder.Render(Packet(playerKnows: many));

        Assert.True(ScenePacketBuilder.EstimateTokens(text) <= ScenePacketBuilder.TokenBudget);
        Assert.Contains("thing number 2000", text, StringComparison.Ordinal);
        Assert.DoesNotContain("thing number 1\n", text, StringComparison.Ordinal);
        Assert.Contains("## What must happen", text, StringComparison.Ordinal);
    }
}
