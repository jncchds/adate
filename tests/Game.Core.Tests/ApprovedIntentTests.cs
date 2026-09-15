using Game.Core;
using Game.Core.Content;

namespace Game.Core.Tests;

/// <summary>
/// The content gate. It sits where a scene becomes an image job, not inside the prompt
/// compiler: what may be depicted is decided by the story side, where the character's age is
/// known, and the image pipeline executes a task that has already been vetted.
/// </summary>
public class ApprovedIntentTests
{
    private static ApprovedIntent Approve(string outfit, Ceiling ceiling) =>
        ApprovedIntent.Approve(
            ContentPolicy.Resolve(new GameContentSettings(18, ceiling), 24, ceiling, Intimacy.None),
            TestContent.Intent() with { Outfit = outfit },
            TestContent.Pack());

    /// <summary>
    /// Why the gate exists at all. Measured on Illustrious and NoobAI: a positive prompt
    /// asking for something the negative list forbids wins, moving only 4.8-6.4% against the
    /// same prompt with those negatives removed, where the same models remove a cafe from a
    /// background at 24-29%. A ceiling made of subtraction is not a ceiling.
    /// </summary>
    [Fact]
    public void An_uncovering_term_is_removed_below_its_ceiling()
    {
        var approved = Approve("nude, sun hat", Ceiling.Suggestive);

        Assert.DoesNotContain("nude", approved.Intent.Outfit, StringComparison.OrdinalIgnoreCase);

        // Per term: one unusable outfit costs the scene that term, not the player their turn.
        Assert.Contains("sun hat", approved.Intent.Outfit, StringComparison.Ordinal);
        Assert.Contains("nude", approved.Removed);
    }

    /// <summary>
    /// The line is coverage, not revealingness. Ordinary swimwear and short or tight clothing
    /// are not what a content ceiling is for, and an earlier draft that restricted them was
    /// wrong.
    /// </summary>
    [Theory]
    [InlineData("swimsuit")]
    [InlineData("bikini")]
    [InlineData("crop top, miniskirt")]
    public void Ordinary_revealing_clothing_survives_pg13(string outfit)
    {
        var approved = Approve(outfit, Ceiling.PG13);

        Assert.Equal(outfit, approved.Intent.Outfit);
        Assert.Empty(approved.Removed);
    }

    /// <summary>A story place's look is written by the model, so it goes through the gate phrase by phrase too.</summary>
    [Fact]
    public void A_restricted_phrase_in_a_places_look_is_removed_and_the_rest_kept()
    {
        var approved = ApprovedIntent.Approve(
            ContentPolicy.Resolve(new GameContentSettings(18, Ceiling.PG13), 24, Ceiling.PG13, Intimacy.None),
            TestContent.Intent() with { LocationLook = "nude statues, velvet booths" },
            TestContent.Pack());

        Assert.Equal("velvet booths", approved.Intent.LocationLook);
        Assert.Contains("nude statues", approved.Removed);
    }

    [Fact]
    public void A_restricted_term_is_permitted_at_its_own_ceiling()
    {
        var approved = Approve("lingerie", Ceiling.Suggestive);

        Assert.Equal("lingerie", approved.Intent.Outfit);
        Assert.Empty(approved.Removed);
    }

    /// <summary>Compound tags are the obvious way past a list of exact terms.</summary>
    [Theory]
    [InlineData("see-through blouse")]
    [InlineData("partially nude")]
    public void Restriction_matches_compound_tags(string outfit)
    {
        var approved = Approve(outfit, Ceiling.PG13);

        Assert.Empty(approved.Intent.Outfit);
        Assert.Contains(outfit, approved.Removed);
    }

    /// <summary>
    /// The clamp reaches the gate through the decision rather than being re-derived here.
    /// A minor's scene is approved at PG13 whatever the game or the pack allows, so a term
    /// that needs a higher ceiling cannot survive one.
    /// </summary>
    [Fact]
    public void A_minors_scene_is_gated_at_pg13_however_permissive_the_game_is()
    {
        var decision = ContentPolicy.Resolve(
            new GameContentSettings(16, Ceiling.Explicit), 17, Ceiling.Explicit, Intimacy.None);

        var approved = ApprovedIntent.Approve(
            decision,
            TestContent.Intent() with { Outfit = "lingerie" },
            TestContent.Pack());

        Assert.Equal(Ceiling.PG13, approved.Ceiling);
        Assert.Empty(approved.Intent.Outfit);
    }

    /// <summary>
    /// Only the three fields the story model writes are filtered. The rest of a prompt is
    /// authored content or a player-declared attribute, and filtering those would mean
    /// second-guessing the game's own data.
    /// </summary>
    [Fact]
    public void The_location_and_framing_are_left_alone()
    {
        var intent = TestContent.Intent();
        var approved = Approve(intent.Outfit, Ceiling.PG13);

        Assert.Equal(intent.LocationId, approved.Intent.LocationId);
        Assert.Equal(intent.Framing, approved.Intent.Framing);
        Assert.Equal(intent.Time, approved.Intent.Time);
    }
}
