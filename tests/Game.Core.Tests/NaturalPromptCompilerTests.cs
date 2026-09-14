using Game.Core.Characters;
using Game.Core.Content;
using Game.Core.Scenes;
using Game.Core.Style;

namespace Game.Core.Tests;

public class NaturalPromptCompilerTests
{
    private static NaturalPromptCompiler Compiler() => new(TestContent.Locations());

    private static string Positive(
        RenderTarget target,
        CharacterAppearance? appearance = null,
        SceneIntent? intent = null) =>
        Compiler().CompilePositive(
            target is RenderTarget.Background ? appearance : appearance ?? TestContent.Appearance(),
            TestContent.Approved(intent),
            TestContent.NaturalPack(),
            target);

    /// <summary>The prompt feeds the content-addressed cache key (HANDOFF 1.7, 4).</summary>
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
    public void Pack_style_sentence_leads_the_prompt()
    {
        Assert.StartsWith("Anime illustration, soft cel shading. ", Positive(RenderTarget.Sprite), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_sentence_is_capitalised_and_full_stopped()
    {
        var prompt = Positive(RenderTarget.Sprite);

        Assert.EndsWith(".", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("..", prompt, StringComparison.Ordinal);
        Assert.Contains(". A single woman, pale skin, an adult woman", prompt, StringComparison.Ordinal);
    }

    /// <summary>HANDOFF 1.9: the age band precedes every body descriptor, in prose as in tags.</summary>
    [Fact]
    public void Age_band_precedes_every_body_descriptor()
    {
        var prompt = Positive(RenderTarget.Sprite);

        var adult = prompt.IndexOf("an adult woman", StringComparison.Ordinal);

        Assert.True(adult >= 0, "the prompt carries no age band");
        Assert.True(adult < prompt.IndexOf("slim", StringComparison.Ordinal), "the age band must precede the build");
        Assert.True(adult < prompt.IndexOf("tall", StringComparison.Ordinal), "the age band must precede the height");
    }

    [Theory]
    [InlineData(17, "a young adult woman")]
    [InlineData(70, "an older woman")]
    public void Age_reaches_the_prompt_as_words(int age, string expected)
    {
        var prompt = Positive(RenderTarget.Sprite, TestContent.Appearance() with { Age = age });

        Assert.Contains(expected, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain($"{age} years old", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Age_bands_are_subject_specific()
    {
        var prompt = Positive(RenderTarget.Sprite, TestContent.Appearance("male") with { Age = 70 });

        Assert.Contains("an older man", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("woman", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// Measured on Z-Image: in the identity list, declared brown skin rendered fair. Skin tone now
    /// sits beside the subject; build still follows the age band (HANDOFF 1.9).
    /// </summary>
    [Fact]
    public void Skin_tone_follows_the_subject_and_build_follows_the_age_band()
    {
        var prompt = Positive(RenderTarget.Sprite);

        Assert.Contains("A single woman, pale skin, an adult woman, adult proportions, with slim.", prompt, StringComparison.Ordinal);
    }

    /// <summary>Measured: a declared beard was on every portrait and missing from every sprite.</summary>
    [Fact]
    public void Skin_build_and_feature_are_restated_after_the_performance()
    {
        var prompt = Positive(RenderTarget.Sprite);

        var restated = prompt.IndexOf("The same person: pale skin, slim and freckles.", StringComparison.Ordinal);

        Assert.True(restated >= 0, "the prompt does not restate skin, build and feature");
        Assert.True(restated > prompt.IndexOf("Wearing sweater", StringComparison.Ordinal), "the restatement must follow the performance");
        Assert.True(restated < prompt.IndexOf("Only this one person", StringComparison.Ordinal), "the restatement must precede the background sentence");
    }

    [Fact]
    public void The_restatement_leaves_out_a_blank_feature()
    {
        var prompt = Positive(RenderTarget.Sprite, TestContent.Appearance() with { DistinguishingFeature = "" });

        Assert.Contains("The same person: pale skin and slim.", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(" and .", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// The player picks "brown skin" and the character stores it; the pack decides which words draw
    /// it. Measured: "brown skin" rendered peach on three seeds of three, "dark-skinned with dark
    /// brown skin" rendered brown on all three.
    /// </summary>
    [Fact]
    public void Pack_prompt_wording_replaces_the_stored_choice_everywhere_it_is_emitted()
    {
        var natural = TestContent.NaturalPack();
        var female = natural.SubjectFor("female");
        var pack = natural with
        {
            Subjects = new Dictionary<string, SubjectProfile>(StringComparer.OrdinalIgnoreCase)
            {
                ["female"] = female with
                {
                    Features = new Dictionary<string, IReadOnlyList<FeatureOption>>
                    {
                        [AppearanceFeatures.SkinTone] = [new FeatureOption("brown skin", Prompt: "dark-skinned with dark brown skin")],
                    },
                },
            },
        };

        var prompt = Compiler().CompilePositive(
            TestContent.Appearance() with { SkinTone = "brown skin" },
            TestContent.Approved(),
            pack,
            RenderTarget.Sprite);

        Assert.Contains("A single woman, dark-skinned with dark brown skin, an adult woman", prompt, StringComparison.Ordinal);
        Assert.Contains("The same person: dark-skinned with dark brown skin, slim and freckles.", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(", brown skin", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Hair_colour_leads_the_identity_sentence()
    {
        var prompt = Positive(RenderTarget.Sprite);

        Assert.Contains(". Red hair, long hair, green eyes", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// Measured on Z-Image: the space beside a half-body subject was filled with a second figure,
    /// and matting kept it. The sprite says outright that there is one person on a plain ground.
    /// </summary>
    [Fact]
    public void Sprite_asks_for_one_person_on_a_plain_background()
    {
        var prompt = Positive(RenderTarget.Sprite);

        Assert.Contains("Only this one person", prompt, StringComparison.Ordinal);
        Assert.Contains("plain flat light grey background", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("cafe", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Outfit_is_worn_and_omitted_when_blank()
    {
        Assert.Contains("Wearing sweater.", Positive(RenderTarget.Sprite), StringComparison.Ordinal);
        Assert.DoesNotContain("Wearing", Positive(RenderTarget.Sprite, intent: TestContent.Intent() with { Outfit = "" }), StringComparison.Ordinal);
    }

    [Fact]
    public void Background_describes_the_place_and_no_one_in_it()
    {
        var prompt = Positive(RenderTarget.Background);

        Assert.Contains("The interior of a cozy corner cafe", prompt, StringComparison.Ordinal);
        Assert.Contains("Golden sunset light", prompt, StringComparison.Ordinal);
        Assert.Contains("no people", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("red hair", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("woman", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Background_describes_the_places_details()
    {
        var prompt = Positive(RenderTarget.Background, intent: TestContent.Intent() with { LocationDetails = ["window-seat"] });

        Assert.Contains("With a sunny window seat.", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void A_detail_the_place_type_does_not_offer_is_refused_by_name()
    {
        var ex = Assert.Throws<KeyNotFoundException>(() =>
            Positive(RenderTarget.Background, intent: TestContent.Intent() with { LocationDetails = ["hot-tub"] }));

        Assert.Contains("hot-tub", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Booru tags read as prose would be a quietly worse background, cached for the life of the
    /// save. A location written only for booru packs is refused instead.
    /// </summary>
    [Fact]
    public void A_location_without_a_description_is_refused_by_name()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Positive(RenderTarget.Background, intent: TestContent.Intent() with { LocationId = "bare" }));

        Assert.Contains("bare", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Character_targets_require_an_appearance()
    {
        Assert.Throws<ArgumentNullException>(() => Compiler().CompilePositive(
            appearance: null,
            TestContent.Approved(),
            TestContent.NaturalPack(),
            RenderTarget.Sprite));
    }

    [Fact]
    public void Blank_attributes_leave_no_empty_phrases()
    {
        var prompt = Positive(RenderTarget.Sprite, TestContent.Appearance() with { DistinguishingFeature = "   " });

        Assert.DoesNotContain(", ,", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("freckles", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Appearance_below_the_absolute_floor_is_rejected_before_a_prompt_exists()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Positive(RenderTarget.Sprite, TestContent.Appearance() with { Age = 15 }));
    }

    [Theory]
    [InlineData(Framing.Portrait, "a close-up portrait of the head and shoulders")]
    [InlineData(Framing.Bust, "an upper body shot from the chest up")]
    [InlineData(Framing.HalfBody, "a half body shot from the waist up")]
    [InlineData(Framing.FullBody, "a full body shot from head to toe")]
    public void Framing_maps_to_its_phrase(Framing framing, string expected)
    {
        var prompt = Positive(RenderTarget.Sprite, intent: TestContent.Intent() with { Framing = framing });

        Assert.Contains(expected, prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("female")]
    [InlineData("male")]
    public void Always_negative_terms_are_present_for_every_subject(string subject)
    {
        var pack = TestContent.NaturalPack();
        var negative = Compiler().CompileNegative(pack, Ceiling.PG13, RenderTarget.Sprite, subject);

        foreach (var term in pack.AlwaysNegative)
        {
            Assert.Contains(term, negative.Split(", "), StringComparer.Ordinal);
        }
    }

    /// <summary>Measured: at cfg 1.0 Z-Image renders a negative byte-identical to none.</summary>
    [Theory]
    [InlineData(RenderTarget.Sprite, "female")]
    [InlineData(RenderTarget.Background, null)]
    public void A_pack_that_ignores_negatives_compiles_none(RenderTarget target, string? subject)
    {
        var pack = TestContent.NaturalPack() with { NegativePrompts = NegativeSupport.Ignored };

        Assert.Equal("", Compiler().CompileNegative(pack, Ceiling.PG13, target, subject));
        Assert.Equal("", new BooruPromptCompiler(TestContent.Locations()).CompileNegative(pack, Ceiling.PG13, target, subject));
    }

    [Fact]
    public void Sprite_negative_names_extra_people()
    {
        var negative = Compiler().CompileNegative(TestContent.NaturalPack(), Ceiling.PG13, RenderTarget.Sprite, "female");

        Assert.Contains("second person", negative, StringComparison.Ordinal);
        Assert.Contains("man", negative.Split(", "), StringComparer.Ordinal);
    }

    [Fact]
    public void Background_negative_names_people()
    {
        var negative = Compiler().CompileNegative(TestContent.NaturalPack(), Ceiling.PG13, RenderTarget.Background, null);

        Assert.Contains("people", negative.Split(", "), StringComparer.Ordinal);
    }

    [Fact]
    public void Unsupported_ceiling_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Compiler().CompileNegative(TestContent.NaturalPack(), Ceiling.Explicit, RenderTarget.Sprite, "female"));
    }

    [Fact]
    public void Character_negative_requires_a_subject()
    {
        Assert.Throws<ArgumentException>(() =>
            Compiler().CompileNegative(TestContent.NaturalPack(), Ceiling.PG13, RenderTarget.Sprite, null));
    }

    [Fact]
    public void Compilers_are_chosen_by_the_pack_dialect()
    {
        var compilers = new PromptCompilers([new BooruPromptCompiler(TestContent.Locations()), Compiler()]);

        Assert.IsType<NaturalPromptCompiler>(compilers.For(TestContent.NaturalPack().Dialect));
        Assert.IsType<BooruPromptCompiler>(compilers.For(TestContent.Pack().Dialect));
    }

    [Fact]
    public void A_dialect_with_no_compiler_is_refused()
    {
        var compilers = new PromptCompilers([Compiler()]);

        var ex = Assert.Throws<InvalidOperationException>(() => compilers.For(PromptDialect.Booru));

        Assert.Contains("Booru", ex.Message, StringComparison.Ordinal);
    }
}
