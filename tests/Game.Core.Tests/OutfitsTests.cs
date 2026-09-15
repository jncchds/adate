using Game.Core.Story;

namespace Game.Core.Tests;

public class OutfitsTests
{
    [Fact]
    public void By_the_water_people_wear_summer_clothes_and_swimwear_is_only_offered()
    {
        var outfit = Outfits.For("Samantha", DressCode.Waterfront, firstDate: false, kept: null, cameFromDress: null, cameFromName: null);

        Assert.Equal(DressCode.Waterfront, outfit.Wearing.Dress);
        Assert.Contains(DressCode.Swim, outfit.Codes);
        Assert.False(outfit.Kept);
    }

    [Fact]
    public void Someone_just_with_the_player_keeps_what_they_wore_and_what_they_put_on()
    {
        var pier = new Outfit(DressCode.Waterfront, "the player's denim jacket");

        var outfit = Outfits.For("Samantha", DressCode.Casual, firstDate: false, kept: pier, cameFromDress: null, cameFromName: "The old pier");

        Assert.True(outfit.Kept);
        Assert.Equal(pier, outfit.Wearing);
        Assert.Equal([DressCode.Waterfront], outfit.Codes);
        Assert.Equal("The old pier", outfit.CameFrom);
    }

    [Fact]
    public void Straight_from_work_people_are_still_in_work_clothes_but_not_after_going_home()
    {
        var fromWork = Outfits.For("Maya", DressCode.Casual, firstDate: false, kept: null, DressCode.Work, "Meridian & Co. offices");
        var fromHome = Outfits.For("Maya", DressCode.Casual, firstDate: false, kept: null, DressCode.Home, "Maya's loft");

        Assert.Equal(DressCode.Work, fromWork.Wearing.Dress);
        Assert.Equal([DressCode.Casual, DressCode.Work], fromWork.Codes);
        Assert.Equal(DressCode.Casual, fromHome.Wearing.Dress);
        Assert.DoesNotContain(DressCode.Home, fromHome.Codes);
    }

    [Fact]
    public void A_first_date_dresses_up_where_people_dress_up_anyway()
    {
        Assert.Equal(DressCode.Date, Outfits.For("Rin", DressCode.Evening, firstDate: true, null, null, null).Wearing.Dress);
        Assert.Equal(DressCode.Camp, Outfits.For("Rin", DressCode.Camp, firstDate: true, null, null, null).Wearing.Dress);
    }

    [Fact]
    public void An_answer_keeps_only_an_offered_code_and_a_short_run_of_words_over_it()
    {
        var offered = Outfits.For("Samantha", DressCode.Waterfront, firstDate: false, null, null, null);

        Assert.Equal(new Outfit(DressCode.Swim), Outfits.Accept(offered, "Swim", ""));
        Assert.Equal(new Outfit(DressCode.Waterfront, "the player's jacket"), Outfits.Accept(offered, "waterfront", "  the player's   jacket. "));
        Assert.Null(Outfits.Accept(offered, DressCode.Evening, ""));
        Assert.Null(Outfits.Accept(null, DressCode.Waterfront, ""));
        Assert.Null(Outfits.CleanOver("<lora:x>"));

        var cut = Outfits.CleanOver(string.Join(' ', Enumerable.Repeat("woolly", 20)))!;
        Assert.True(cut.Length <= Outfits.MaxOverLength);
        Assert.EndsWith("woolly", cut, StringComparison.Ordinal);
    }

    [Fact]
    public void The_writer_hears_what_they_still_wear_and_is_asked_to_pick_among_the_codes()
    {
        var packet = new ScenePacket(
            "Small town", "Slow summer.", new World.ClockState(3, Scenes.TimeOfDay.Night), "lookout-hill", "Lookout Hill", "Alex",
            [new PacketPerson("sam-id", "Samantha", ["Speaks evenly."], RelationshipStage.Acquaintance, null)],
            [], [], "Samantha is here with the player.", Ceiling.PG13, ["neutral", "smile"],
            Outfit: Outfits.For("Samantha", DressCode.Casual, false, new Outfit(DressCode.Waterfront, "a borrowed jacket"), null, "The old pier"));

        var text = ScenePacketBuilder.Render(packet);

        Assert.Contains("Samantha is still wearing light summer clothes, not swimwear, with a borrowed jacket over it", text, StringComparison.Ordinal);
        Assert.Contains("- outfit: what Samantha wears here. dress: one of waterfront", text, StringComparison.Ordinal);
        Assert.Contains("\"a borrowed jacket\" while they still have it on", text, StringComparison.Ordinal);

        var settled = ScenePacketBuilder.Render(packet with { Outfit = packet.Outfit! with { Settled = true } });
        Assert.Contains("Samantha is wearing light summer clothes", settled, StringComparison.Ordinal);
        Assert.DoesNotContain("- outfit:", settled, StringComparison.Ordinal);
    }
}
