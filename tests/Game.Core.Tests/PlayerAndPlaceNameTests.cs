using Game.Core.Places;
using Game.Core.Story;

namespace Game.Core.Tests;

public class PlayerAndPlaceNameTests
{
    [Theory]
    [InlineData("woman", "she/her")]
    [InlineData("Man", "he/him")]
    [InlineData("nonbinary", "they/them")]
    [InlineData(null, null)]
    [InlineData("", null)]
    public void The_players_gender_gives_the_pronouns_others_use(string? gender, string? pronouns) =>
        Assert.Equal(pronouns, PlayerPronouns.For(gender));

    [Theory]
    [InlineData("The rooftop garden", "rooftop garden")]
    [InlineData("Paper Lantern Books", "paper-lantern books")]
    [InlineData("  the Corner Cup ", "The Corner Cup")]
    public void Place_names_that_differ_only_in_form_are_the_same(string a, string b) =>
        Assert.True(PlaceProposals.SameName(a, b));

    [Theory]
    [InlineData("The rooftop garden", "The rooftop bar")]
    [InlineData("Theatre Row", "Row")]
    public void Different_names_stay_different(string a, string b) =>
        Assert.False(PlaceProposals.SameName(a, b));

    [Theory]
    [InlineData("Кофейня «У Рози»", "Муниципальная библиотека")]
    [InlineData("Холм Видов", "Дом у озера")]
    [InlineData("居酒屋", "図書館")]
    public void Places_named_in_another_alphabet_are_told_apart(string a, string b) =>
        // Comparing only Latin letters reduced every one of these to nothing, so a Russian story could not
        // name two places: the second was always refused as one it already had.
        Assert.False(PlaceProposals.SameName(a, b));

    [Theory]
    [InlineData("Кофейня «У Рози»", "кофейня У Рози")]
    [InlineData("Холм Видов", "  Холм  Видов  ")]
    public void The_same_name_in_another_alphabet_is_still_the_same(string a, string b) =>
        Assert.True(PlaceProposals.SameName(a, b));

    [Theory]
    [InlineData("rustic brick interior, cozy seating", true)]
    [InlineData("café interior, wrought-iron tables", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("краснокирпичные стены, большие окна", false)]
    [InlineData("high ceilings, тишина и пылинки", false)]
    public void A_look_is_drawable_only_when_the_image_model_can_read_it(string? look, bool drawable) =>
        // The name is in the story's language; the look is fed to an image model that reads English alone.
        Assert.Equal(drawable, PlaceProposals.IsDrawable(look));
}
