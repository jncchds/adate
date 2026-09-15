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
    public void The_writer_is_told_what_the_player_does_with_their_life()
    {
        var text = ScenePacketBuilder.Render(Packet() with { PlayerLife = ["Alex works as a junior analyst.", "Alex often swims in the lake."] });

        Assert.Contains("Alex works as a junior analyst.", text, StringComparison.Ordinal);
        Assert.Contains("Alex often swims in the lake.", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("Alex often swims", StringComparison.Ordinal) < text.IndexOf("## What Alex knows", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_pass_writing_splits_the_rules_between_the_prose_and_the_data_and_both_see_the_material()
    {
        var rin = Packet().Present[0] with { Voice = "Short sentences, never a question answered directly." };
        var packet = Packet() with
        {
            Present = [rin],
            OffersChoices = true,
            LooseEnds = [new PacketThread(7, "Rin promised to lend the player a record.", 5)],
            Happening = "The espresso machine has broken down.",
            VariedChoices = true,
        };

        var all = ScenePacketBuilder.Render(packet);
        var prose = ScenePacketBuilder.Render(packet, ScenePart.Prose);
        var extract = ScenePacketBuilder.Render(packet, ScenePart.Extract);
        var context = ScenePacketBuilder.Render(packet, ScenePart.Context);

        Assert.Contains("prose only", prose, StringComparison.Ordinal);
        Assert.Contains("Never say what the player does", prose, StringComparison.Ordinal);
        Assert.DoesNotContain("- facts:", prose, StringComparison.Ordinal);
        Assert.DoesNotContain("- threads:", prose, StringComparison.Ordinal);

        Assert.Contains("already written", extract, StringComparison.Ordinal);
        Assert.Contains("- facts:", extract, StringComparison.Ordinal);
        Assert.Contains("- threads:", extract, StringComparison.Ordinal);
        Assert.DoesNotContain("Never say what the player does", extract, StringComparison.Ordinal);

        Assert.DoesNotContain("## Rules", context, StringComparison.Ordinal);
        Assert.Contains("different in kind", all, StringComparison.Ordinal);

        Assert.All(new[] { all, prose, extract, context }, text =>
        {
            Assert.Contains("#7 (day 5): Rin promised to lend the player a record.", text, StringComparison.Ordinal);
            Assert.Contains("Also going on here right now: The espresso machine has broken down.", text, StringComparison.Ordinal);
            Assert.Contains("How Rin talks: Short sentences, never a question answered directly.", text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Data_read_from_a_scene_in_another_language_keeps_its_choices_in_that_language()
    {
        var extract = ScenePacketBuilder.Render(Packet() with { Language = "Русский", PlayerGender = "man", OffersChoices = true }, ScenePart.Extract);

        Assert.Contains("- Write the summary and choices in Русский", extract, StringComparison.Ordinal);
        Assert.Contains("masculine forms for the player", extract, StringComparison.Ordinal);
        Assert.DoesNotContain("Write the prose in", extract, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_the_material_nothing_of_it_is_rendered()
    {
        var text = ScenePacketBuilder.Render(Packet() with { OffersChoices = true });

        Assert.DoesNotContain("## Loose ends", text, StringComparison.Ordinal);
        Assert.DoesNotContain("- threads:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Also going on", text, StringComparison.Ordinal);
        Assert.DoesNotContain("talks:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("different in kind", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Alone_the_choices_are_things_to_do_here_and_a_shift_makes_the_scene_about_work()
    {
        var alone = ScenePacketBuilder.Render(Packet() with { Present = [], OffersChoices = true, Duty = "stock the shelves" });
        var together = ScenePacketBuilder.Render(Packet() with { OffersChoices = true });

        Assert.Contains("things the player could do here now", alone, StringComparison.Ordinal);
        Assert.Contains("the work they are here for", alone, StringComparison.Ordinal);
        Assert.Contains("is here for their shift, supposed to stock the shelves", alone, StringComparison.Ordinal);
        Assert.Contains("things the player could say or do next", together, StringComparison.Ordinal);
        Assert.DoesNotContain("here for their shift", together, StringComparison.Ordinal);
    }

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
