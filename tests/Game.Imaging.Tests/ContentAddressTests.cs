using Game.Core;
using Game.Imaging;
using Game.Imaging.Caching;

namespace Game.Imaging.Tests;

public class ContentAddressTests
{
    private static ImageRequest Baseline() => new(
        WorkflowId: "sprite",
        Positive: "1girl, blue eyes",
        Negative: "lowres",
        Seed: 12345,
        Width: 768,
        Height: 1152,
        PackFingerprint: "packfingerprint",
        AnchorImageHash: "anchorhash",
        AnchorWeight: 0.85,
        Ceiling: Ceiling.PG13);

    [Fact]
    public void Address_is_a_lowercase_sha256()
    {
        var hash = ContentAddress.For(Baseline());

        Assert.Equal(64, hash.Length);
        Assert.True(ContentAddress.IsWellFormed(hash));
    }

    [Fact]
    public void Identical_requests_address_the_same_file()
    {
        Assert.Equal(ContentAddress.For(Baseline()), ContentAddress.For(Baseline()));
    }

    public static TheoryData<string, ImageRequest> Variations() => new()
    {
        { "workflow", Baseline() with { WorkflowId = "portrait" } },
        { "pack", Baseline() with { PackFingerprint = "other" } },
        { "positive", Baseline() with { Positive = "1girl, green eyes" } },
        { "negative", Baseline() with { Negative = "lowres, worst quality" } },
        { "seed", Baseline() with { Seed = 12346 } },
        { "width", Baseline() with { Width = 512 } },
        { "height", Baseline() with { Height = 512 } },
        { "anchor", Baseline() with { AnchorImageHash = "different" } },
        { "anchor weight", Baseline() with { AnchorWeight = 0.9 } },
        { "ceiling", Baseline() with { Ceiling = Ceiling.Suggestive } },
    };

    /// <summary>
    /// Every field must reach the key. A field silently missing from the digest means the
    /// cache serves art generated under different parameters, which is the failure mode
    /// HANDOFF 1.8 calls out specifically for <see cref="Ceiling"/>.
    /// </summary>
    [Theory]
    [MemberData(nameof(Variations))]
    public void Changing_any_parameter_changes_the_address(string field, ImageRequest changed)
    {
        Assert.NotEqual(ContentAddress.For(Baseline()), ContentAddress.For(changed));
        Assert.True(true, field);
    }

    /// <summary>
    /// Guards against a naive digest that concatenates fields without a delimiter, where
    /// moving characters across a field boundary would produce a collision.
    /// </summary>
    [Fact]
    public void Field_boundaries_cannot_be_shifted_to_collide()
    {
        var a = Baseline() with { Positive = "ab", Negative = "c" };
        var b = Baseline() with { Positive = "a", Negative = "bc" };

        Assert.NotEqual(ContentAddress.For(a), ContentAddress.For(b));
    }

    [Fact]
    public void Absent_anchor_differs_from_empty_anchor_only_when_meaningful()
    {
        var none = Baseline() with { AnchorImageHash = null, AnchorWeight = null };
        var alsoNone = Baseline() with { AnchorImageHash = null, AnchorWeight = null };

        Assert.Equal(ContentAddress.For(none), ContentAddress.For(alsoNone));
        Assert.NotEqual(ContentAddress.For(none), ContentAddress.For(Baseline()));
    }

    [Fact]
    public void IsWellFormed_rejects_uppercase_and_wrong_length()
    {
        Assert.False(ContentAddress.IsWellFormed(new string('A', 64)));
        Assert.False(ContentAddress.IsWellFormed(new string('a', 63)));
        Assert.True(ContentAddress.IsWellFormed(new string('a', 64)));
    }
}
