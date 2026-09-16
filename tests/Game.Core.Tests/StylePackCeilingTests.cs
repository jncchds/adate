using Game.Core.Style;

namespace Game.Core.Tests;

/// <summary>
/// A save chooses how far it goes, and the pack is clamped to it before anything is rendered
/// (user feedback: there was no content toggle, and it should start off).
/// </summary>
public class StylePackCeilingTests
{
    private static StylePack Pack(params Ceiling[] ceilings) =>
        TestContent.Pack() with { SupportedCeilings = ceilings };

    [Fact]
    public void A_pack_is_clamped_to_the_saves_ceiling()
    {
        var pack = Pack(Ceiling.PG13, Ceiling.Suggestive, Ceiling.Explicit).ClampedTo(Ceiling.Suggestive);

        Assert.Equal([Ceiling.PG13, Ceiling.Suggestive], pack.SupportedCeilings);
        Assert.Equal(Ceiling.Suggestive, pack.HighestCeiling);
        Assert.False(pack.Supports(Ceiling.Explicit));
    }

    [Fact]
    public void A_pack_that_already_stops_lower_is_left_alone()
    {
        var pack = Pack(Ceiling.PG13);

        Assert.Same(pack, pack.ClampedTo(Ceiling.Explicit));
    }

    /// <summary>A pack that renders nothing is worse than one that renders its mildest.</summary>
    [Fact]
    public void A_pack_starting_above_the_ceiling_keeps_its_lowest()
    {
        var pack = Pack(Ceiling.Suggestive, Ceiling.Explicit).ClampedTo(Ceiling.PG13);

        Assert.Equal([Ceiling.Suggestive], pack.SupportedCeilings);
    }
}
