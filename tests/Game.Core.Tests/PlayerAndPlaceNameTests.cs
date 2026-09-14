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
}
