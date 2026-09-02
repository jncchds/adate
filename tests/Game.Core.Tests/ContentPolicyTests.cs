using Game.Core;
using Game.Core.Content;

namespace Game.Core.Tests;

public class ContentPolicyTests
{
    private static GameContentSettings TeenGame(Ceiling max = Ceiling.Explicit) => new(16, max);

    private static GameContentSettings AdultGame(Ceiling max = Ceiling.Explicit) => new(18, max);

    /// <summary>
    /// The invariant the whole type exists for. Exhaustive rather than sampled: every
    /// permitted under-18 age, against every game setting, pack ceiling and requested
    /// intimacy, must come back clamped and undepicted.
    /// </summary>
    [Fact]
    public void No_configuration_lifts_the_clamp_for_a_minor()
    {
        Ceiling[] ceilings = [Ceiling.PG13, Ceiling.Suggestive, Ceiling.Explicit];
        Intimacy[] intimacies = [Intimacy.None, Intimacy.Suggestive, Intimacy.Explicit];

        for (var age = ContentPolicy.LowestPermittedMinimumAge; age < ContentPolicy.AdultAge; age++)
        {
            foreach (var gameMax in ceilings)
            {
                foreach (var packCeiling in ceilings)
                {
                    foreach (var requested in intimacies)
                    {
                        var decision = ContentPolicy.Resolve(
                            new GameContentSettings(ContentPolicy.LowestPermittedMinimumAge, gameMax),
                            age,
                            packCeiling,
                            requested);

                        Assert.Equal(Ceiling.PG13, decision.Ceiling);

                        if (requested is not Intimacy.None)
                        {
                            Assert.False(decision.Depict);
                            Assert.Equal(Narration.FadeToBlack, decision.Narration);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Fading the image while writing the scene in detail is not a fade to black. The two
    /// have to move together, so this is asserted separately from the ceiling itself.
    /// </summary>
    [Theory]
    [InlineData(Intimacy.Suggestive)]
    [InlineData(Intimacy.Explicit)]
    public void A_minors_intimacy_is_never_narrated_in_detail(Intimacy requested)
    {
        var decision = ContentPolicy.Resolve(TeenGame(), 17, Ceiling.Explicit, requested);

        Assert.Equal(Narration.FadeToBlack, decision.Narration);
        Assert.False(decision.Depict);
    }

    [Fact]
    public void A_minor_can_still_have_a_non_intimate_scene()
    {
        var decision = ContentPolicy.Resolve(TeenGame(), 16, Ceiling.Explicit, Intimacy.None);

        Assert.True(decision.Depict);
        Assert.Equal(Ceiling.PG13, decision.Ceiling);
        Assert.Equal(Narration.Full, decision.Narration);
    }

    [Fact]
    public void An_adult_in_an_adult_game_may_be_depicted()
    {
        var decision = ContentPolicy.Resolve(AdultGame(), 24, Ceiling.Explicit, Intimacy.Explicit);

        Assert.True(decision.Depict);
        Assert.Equal(Ceiling.Explicit, decision.Ceiling);
    }

    /// <summary>The adult content toggle: an adult in a PG13 game fades rather than fails.</summary>
    [Fact]
    public void An_adult_in_a_pg13_game_fades_to_black()
    {
        var decision = ContentPolicy.Resolve(AdultGame(Ceiling.PG13), 24, Ceiling.Explicit, Intimacy.Explicit);

        Assert.False(decision.Depict);
        Assert.Equal(Narration.FadeToBlack, decision.Narration);
        Assert.Equal(Ceiling.PG13, decision.Ceiling);
    }

    /// <summary>
    /// A pack that will not render a ceiling caps it too, so an adult game paired with a
    /// PG13-only pack behaves as a PG13 game rather than sending the pack something it
    /// declared it would refuse.
    /// </summary>
    [Fact]
    public void The_pack_ceiling_caps_an_adult_scene()
    {
        var decision = ContentPolicy.Resolve(AdultGame(), 24, Ceiling.PG13, Intimacy.Explicit);

        Assert.Equal(Ceiling.PG13, decision.Ceiling);
        Assert.False(decision.Depict);
    }

    [Fact]
    public void A_game_may_not_declare_a_minimum_below_sixteen()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GameContentSettings(15, Ceiling.PG13).Validate());
    }

    /// <summary>
    /// A character below the game's own floor is a data error, not something to clamp
    /// quietly. Silently accepting it would let a game's declared floor drift.
    /// </summary>
    [Fact]
    public void A_character_below_the_games_minimum_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ContentPolicy.Resolve(AdultGame(), 17, Ceiling.Explicit, Intimacy.None));
    }

    [Fact]
    public void The_safe_default_is_adults_only_and_pg13()
    {
        Assert.Equal(ContentPolicy.AdultAge, GameContentSettings.SafeDefault.MinimumCharacterAge);
        Assert.Equal(Ceiling.PG13, GameContentSettings.SafeDefault.MaxCeiling);
    }
}
