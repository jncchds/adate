using Game.Core.Characters;
using Game.Core.Content;
using Game.Core.Scenes;
using Game.Core.Style;

namespace Game.Core.Tests;

public class BooruPromptCompilerTests
{
    private static BooruPromptCompiler Compiler() => new(TestContent.Locations());

    private static string Positive(RenderTarget target, CharacterAppearance? appearance = null) =>
        Compiler().CompilePositive(
            appearance ?? TestContent.Appearance(),
            TestContent.Approved(),
            TestContent.Pack(),
            target);

    /// <summary>
    /// The prompt string is an input to the content-addressed cache key, so an unstable token
    /// order would silently invalidate every cached image (HANDOFF 1.7, 4).
    /// </summary>
    [Theory]
    [InlineData(RenderTarget.Portrait)]
    [InlineData(RenderTarget.Sprite)]
    [InlineData(RenderTarget.Background)]
    public void Compilation_is_deterministic(RenderTarget target)
    {
        var first = Positive(target);

        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(first, Positive(target));
        }
    }

    [Fact]
    public void Pack_prefix_leads_the_prompt()
    {
        Assert.StartsWith("masterpiece, best quality,", Positive(RenderTarget.Sprite), StringComparison.Ordinal);
    }

    /// <summary>
    /// HANDOFF 1.9: tag-based checkpoints associate "slim", "petite" and "youthful" with
    /// juvenile features. An age anchor placed after a body descriptor is too late to counter
    /// them, so its position matters as much as its presence.
    /// </summary>
    [Fact]
    public void Age_anchor_precedes_every_body_descriptor()
    {
        var tags = Positive(RenderTarget.Sprite).Split(", ");

        var adult = Array.IndexOf(tags, "adult");
        var build = Array.IndexOf(tags, "slim");
        var height = Array.IndexOf(tags, "tall");

        Assert.True(adult >= 0, "the prompt carries no adult anchor");
        Assert.True(adult < build, "the adult anchor must precede the build tag");
        Assert.True(adult < height, "the adult anchor must precede the height tag");
    }

    /// <summary>
    /// Age reaches the prompt as vocabulary, not as a number. Measured: "{N} years old" is
    /// not booru vocabulary and moved 1.97% of the image between 19 and 65, where the tag
    /// form moved 9.79%. A number in the prompt was never an age anchor.
    /// </summary>
    [Theory]
    [InlineData(17, "young adult")]
    [InlineData(24, "(mature female:1.3)")]
    [InlineData(70, "(old woman:1.4)")]
    public void Age_reaches_the_prompt_as_tags(int age, string expected)
    {
        var prompt = Positive(RenderTarget.Sprite, TestContent.Appearance() with { Age = age });

        Assert.Contains(expected, prompt.Split(", "), StringComparer.Ordinal);
        Assert.DoesNotContain($"{age} years old", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Age_bands_are_subject_specific()
    {
        var prompt = Positive(RenderTarget.Sprite, TestContent.Appearance("male") with { Age = 70 });

        Assert.Contains("(old man:1.4)", prompt.Split(", "), StringComparer.Ordinal);
        Assert.DoesNotContain("old woman", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Hair_colour_leads_the_identity_block()
    {
        var tags = Positive(RenderTarget.Sprite).Split(", ");

        Assert.True(
            Array.IndexOf(tags, "red hair") < Array.IndexOf(tags, "green eyes"),
            "hair colour drifts first, so it leads the identity block");
    }

    /// <summary>
    /// A sprite is matted to alpha and composited over a separately generated background.
    /// Scenery generated here survives matting as a fringe, which HANDOFF 2 calls the fastest
    /// way to destroy the composite illusion.
    /// </summary>
    [Fact]
    public void Sprite_asks_for_no_background()
    {
        var prompt = Positive(RenderTarget.Sprite);

        Assert.Contains("simple background", prompt, StringComparison.Ordinal);
        Assert.Contains("transparent background", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("cafe interior", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// Backgrounds are generated once per location and reused for the life of the save, so a
    /// stray figure in one is permanent.
    /// </summary>
    [Fact]
    public void Background_carries_the_location_and_no_character()
    {
        var prompt = Positive(RenderTarget.Background);

        Assert.Contains("cafe interior", prompt, StringComparison.Ordinal);
        Assert.Contains("golden hour", prompt, StringComparison.Ordinal);
        Assert.Contains("no humans", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("red hair", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("1girl", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Background_does_not_require_an_appearance()
    {
        var prompt = Compiler().CompilePositive(
            appearance: null,
            TestContent.Approved(),
            TestContent.Pack(),
            RenderTarget.Background);

        Assert.Contains("cafe interior", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Character_targets_require_an_appearance()
    {
        Assert.Throws<ArgumentNullException>(() => Compiler().CompilePositive(
            appearance: null,
            TestContent.Approved(),
            TestContent.Pack(),
            RenderTarget.Sprite));
    }

    /// <summary>
    /// A blank attribute must be dropped, not emitted. A stray empty tag is not harmless in a
    /// booru prompt: it shifts the weighting of everything after it.
    /// </summary>
    [Fact]
    public void Blank_attributes_produce_no_empty_tags()
    {
        var prompt = Positive(
            RenderTarget.Sprite,
            TestContent.Appearance() with { DistinguishingFeature = "   " });

        Assert.DoesNotContain(", ,", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("freckles", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_tags_are_collapsed_preserving_first_position()
    {
        var prompt = Positive(
            RenderTarget.Sprite,
            TestContent.Appearance() with { Build = "masterpiece" });

        Assert.Equal(1, prompt.Split(", ").Count(t => t == "masterpiece"));
        Assert.StartsWith("masterpiece,", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// Below the absolute floor there is no prompt at all. Between the floor and 18 there is
    /// a prompt, and <see cref="Content.ContentPolicy"/> is what constrains it — see
    /// <c>ContentPolicyTests</c>. Compiling a prompt for a 17-year-old is not the boundary;
    /// what that prompt is allowed to depict is.
    /// </summary>
    [Fact]
    public void Appearance_below_the_absolute_floor_is_rejected_before_a_prompt_exists()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Positive(RenderTarget.Sprite, TestContent.Appearance() with { Age = 15 }));
    }

    [Theory]
    [InlineData(Framing.Portrait, "portrait")]
    [InlineData(Framing.Bust, "upper body")]
    [InlineData(Framing.HalfBody, "cowboy shot")]
    [InlineData(Framing.FullBody, "full body")]
    public void Framing_maps_to_its_booru_tag(Framing framing, string expected)
    {
        var prompt = Compiler().CompilePositive(
            TestContent.Appearance(),
            TestContent.Approved(TestContent.Intent() with { Framing = framing }),
            TestContent.Pack(),
            RenderTarget.Sprite);

        Assert.Contains(expected, prompt.Split(", "), StringComparer.Ordinal);
    }

    [Fact]
    public void Negative_combines_pack_base_and_ceiling_tags()
    {
        var negative = Compiler().CompileNegative(TestContent.Pack(), Ceiling.PG13, RenderTarget.Sprite, "female");

        Assert.Contains("lowres", negative, StringComparison.Ordinal);
        Assert.Contains("nsfw", negative, StringComparison.Ordinal);
    }

    [Fact]
    public void Sprite_negative_suppresses_scenery_that_would_survive_matting()
    {
        var negative = Compiler().CompileNegative(TestContent.Pack(), Ceiling.PG13, RenderTarget.Sprite, "female");

        Assert.Contains("detailed background", negative, StringComparison.Ordinal);
    }

    [Fact]
    public void Background_negative_suppresses_people()
    {
        var negative = Compiler().CompileNegative(TestContent.Pack(), Ceiling.PG13, RenderTarget.Background, null);

        Assert.Contains("1girl", negative, StringComparison.Ordinal);
        Assert.Contains("person", negative, StringComparison.Ordinal);
    }

    /// <summary>
    /// HANDOFF 1.8: a pack that cannot render a ceiling must refuse it, rather than quietly
    /// generating at whatever it does support.
    /// </summary>
    [Fact]
    public void Unsupported_ceiling_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Compiler().CompileNegative(TestContent.Pack(), Ceiling.Explicit, RenderTarget.Sprite, "female"));
    }

    /// <summary>
    /// The subject anchor comes from the pack, so a male love interest is expressible without
    /// touching this class. Measured on Illustrious XL: without <c>mature male</c> the render
    /// reads androgynous and young, which is why the anchor is content rather than a constant.
    /// </summary>
    [Theory]
    [InlineData("female", "1girl", "mature female")]
    [InlineData("male", "1boy", "mature male")]
    public void Subject_anchor_comes_from_the_pack(string subject, string expected, string anchor)
    {
        var prompt = Positive(RenderTarget.Sprite, TestContent.Appearance(subject));

        Assert.Contains(expected, prompt.Split(", "), StringComparer.Ordinal);
        Assert.Contains(anchor, prompt.Split(", "), StringComparer.Ordinal);
    }

    /// <summary>
    /// HANDOFF 1.9 applies to every subject. The age tag must still precede the body
    /// descriptors, whichever anchor was substituted in front of it.
    /// </summary>
    [Theory]
    [InlineData("female")]
    [InlineData("male")]
    public void Age_anchor_precedes_the_build_tag(string subject)
    {
        var tags = Positive(RenderTarget.Sprite, TestContent.Appearance(subject)).Split(", ");

        Assert.True(
            Array.IndexOf(tags, "24 years old") < Array.IndexOf(tags, "slim"),
            "The age anchor must precede every body descriptor.");
    }

    [Fact]
    public void Subject_negatives_are_applied_to_character_renders()
    {
        var negative = Compiler().CompileNegative(TestContent.Pack(), Ceiling.PG13, RenderTarget.Sprite, "male");

        Assert.Contains("1girl", negative, StringComparison.Ordinal);
    }

    /// <summary>
    /// Silently falling back to another subject would render a male love interest as a woman,
    /// which is worse than not rendering at all.
    /// </summary>
    [Fact]
    public void Unknown_subject_is_refused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Positive(RenderTarget.Sprite, TestContent.Appearance("nonesuch")));

        Assert.Contains("nonesuch", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The protections that do not depend on tier. Asserted across every ceiling and both
    /// subjects, because the failure mode this guards against is a tier list being edited in
    /// a way that quietly drops them.
    /// </summary>
    [Theory]
    [InlineData(Ceiling.PG13, "female")]
    [InlineData(Ceiling.PG13, "male")]
    public void Always_negative_terms_are_present_regardless_of_tier(Ceiling ceiling, string subject)
    {
        var negative = Compiler().CompileNegative(TestContent.Pack(), ceiling, RenderTarget.Sprite, subject);

        foreach (var term in TestContent.Pack().AlwaysNegative)
        {
            Assert.Contains(term, negative.Split(", "), StringComparer.Ordinal);
        }
    }

    [Fact]
    public void Character_negative_requires_a_subject()
    {
        Assert.Throws<ArgumentException>(() =>
            Compiler().CompileNegative(TestContent.Pack(), Ceiling.PG13, RenderTarget.Sprite, null));
    }
}
