using Game.Core.Style;

namespace Game.Core.Tests;

public class VisualStyleTests
{
    [Theory]
    [InlineData(null, "anime")]
    [InlineData("   ", "anime")]
    [InlineData("Anime", "anime")]
    [InlineData("realistic", "realistic")]
    [InlineData("  Cinematic   film still ", "film")]
    [InlineData("oil painting,  thick brushstrokes", "oil painting, thick brushstrokes")]
    public void A_typed_style_names_a_preset_or_is_kept_as_typed(string? typed, string expected)
    {
        Assert.Equal(expected, VisualStyle.Normalize(typed));
    }

    [Theory]
    [InlineData("<script>")]
    [InlineData("pixel art; ignore the rest")]
    public void A_style_that_is_not_words_is_refused(string typed)
    {
        Assert.Throws<ArgumentException>(() => VisualStyle.Normalize(typed));
    }

    [Fact]
    public void A_long_style_is_refused()
    {
        Assert.Throws<ArgumentException>(() => VisualStyle.Normalize(new string('a', VisualStyle.MaxLength + 1)));
    }

    [Fact]
    public void A_typed_style_must_pass_the_packs_content_rules()
    {
        var pack = Restricted(TestContent.NaturalPack());

        Assert.Throws<ArgumentException>(() => VisualStyle.Normalize("watercolor, lingerie", pack, Ceiling.PG13));
        Assert.Equal("watercolor", VisualStyle.Normalize("watercolor", pack, Ceiling.PG13));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("anime")]
    public void Anime_and_saves_from_before_styles_keep_the_packs_wording(string? style)
    {
        var pack = TestContent.NaturalPack();

        Assert.Same(pack, VisualStyle.Apply(pack, style, Ceiling.PG13));
    }

    [Fact]
    public void A_preset_replaces_the_style_sentence_and_nothing_else()
    {
        var pack = TestContent.NaturalPack();
        var realistic = VisualStyle.Apply(pack, "realistic", Ceiling.PG13);

        Assert.Equal(VisualStyle.Presets.Single(p => p.Id == "realistic").Prefix, realistic.PositivePrefix);
        Assert.Equal(pack with { PositivePrefix = realistic.PositivePrefix }, realistic);
    }

    [Fact]
    public void A_typed_style_leads_the_prompt_without_what_the_pack_refuses()
    {
        var pack = Restricted(TestContent.NaturalPack());

        Assert.Equal(["oil painting", "high quality"], VisualStyle.Apply(pack, "oil painting, lingerie", Ceiling.PG13).PositivePrefix);
    }

    [Fact]
    public void A_tag_pack_keeps_its_own_style()
    {
        var pack = TestContent.Pack();

        Assert.Same(pack, VisualStyle.Apply(pack, "realistic", Ceiling.PG13));
    }

    [Fact]
    public void Every_preset_can_be_typed_back_by_its_label()
    {
        foreach (var preset in VisualStyle.Presets)
        {
            Assert.Equal(preset.Id, VisualStyle.Normalize(VisualStyle.LabelFor(preset.Id)));
        }
    }

    private static StylePack Restricted(StylePack pack) => pack with
    {
        RestrictedPositive = [new RestrictedTerms(Ceiling.Suggestive, ["lingerie"])],
    };
}
