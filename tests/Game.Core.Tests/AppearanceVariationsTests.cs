using Game.Core.Characters;
using Game.Core.Style;

namespace Game.Core.Tests;

public class AppearanceVariationsTests
{
    private static SubjectProfile Profile() => new(
        ["1girl", "solo"],
        ["1boy"],
        ["white blouse", "pleated skirt"],
        [new AgeBand(16, ["young adult"])],
        new Dictionary<string, IReadOnlyList<FeatureOption>>
        {
            [AppearanceFeatures.HairColor] =
            [
                new("red hair", ["orange hair", "brown hair"]),
                new("orange hair", ["red hair"]),
                new("brown hair", ["red hair"]),
            ],
            [AppearanceFeatures.HairStyle] = [new("long hair", ["medium hair"]), new("medium hair", ["long hair"])],
            [AppearanceFeatures.EyeColor] = [new("green eyes", ["aqua eyes"]), new("aqua eyes", ["green eyes"])],
            [AppearanceFeatures.SkinTone] = [new("pale skin"), new("tan")],
            [AppearanceFeatures.Build] = [new("average build"), new("athletic")],
            [AppearanceFeatures.Height] = [new("average height"), new("tall")],
        });

    private static CharacterAppearance Declared() => new(
        Subject: "female",
        Age: 24,
        EyeColor: "green eyes",
        HairColor: "red hair",
        HairStyle: "long hair",
        SkinTone: "pale skin",
        Build: "average build",
        Height: "average height",
        DistinguishingFeature: "freckles");

    [Fact]
    public void The_first_alternative_is_exactly_what_was_declared()
    {
        var variants = AppearanceVariations.For(Declared(), Profile(), count: 4, seed: 7);

        Assert.Equal(Declared(), variants[0].Appearance);
        Assert.True(variants[0].IsAsDeclared);
    }

    /// <summary>
    /// The property the whole feature rests on: an alternative is the declared character with
    /// one or two features nudged to a declared neighbour, and nothing else about them moves.
    /// </summary>
    [Fact]
    public void Every_other_alternative_moves_only_variable_features_to_a_declared_neighbour()
    {
        var profile = Profile();
        var declared = Declared();

        foreach (var variant in AppearanceVariations.For(declared, profile, count: 6, seed: 7).Skip(1))
        {
            Assert.InRange(variant.Changes.Count, 1, 2);

            var expected = declared;
            foreach (var change in variant.Changes)
            {
                Assert.Contains(change.Feature, AppearanceFeatures.Variable);
                Assert.Equal(AppearanceFeatures.Get(declared, change.Feature), change.From);

                var neighbours = profile.OptionsFor(change.Feature).Single(o => o.Tag == change.From).Near!;
                Assert.Contains(change.To, neighbours);

                expected = AppearanceFeatures.With(expected, change.Feature, change.To);
            }

            // Nothing beyond the listed changes differs -- in particular age, subject, skin tone,
            // build and height, which an alternative must never touch.
            Assert.Equal(expected, variant.Appearance);
        }
    }

    [Fact]
    public void Alternatives_are_distinct_and_capped_at_the_requested_count()
    {
        var variants = AppearanceVariations.For(Declared(), Profile(), count: 4, seed: 7);

        Assert.Equal(4, variants.Count);
        Assert.Equal(4, variants.Select(v => v.Appearance).Distinct().Count());
    }

    /// <summary>
    /// Candidates are re-requested on every visit to the page. A reshuffle would change what the
    /// player is choosing between and miss the image cache.
    /// </summary>
    [Fact]
    public void The_same_seed_offers_the_same_alternatives()
    {
        var first = AppearanceVariations.For(Declared(), Profile(), count: 6, seed: 12345);
        var second = AppearanceVariations.For(Declared(), Profile(), count: 6, seed: 12345);

        Assert.Equal(first.Select(v => v.Appearance), second.Select(v => v.Appearance));
    }

    /// <summary>
    /// Hair colour has the most neighbours here. Without spreading, it would take every slot
    /// and the player would see three hair colours instead of three looks.
    /// </summary>
    [Fact]
    public void Single_changes_are_spread_across_features_before_any_feature_repeats()
    {
        var variants = AppearanceVariations.For(Declared(), Profile(), count: 4, seed: 7);

        var features = variants.Skip(1).Select(v => Assert.Single(v.Changes).Feature).ToList();

        Assert.Equal(3, features.Distinct().Count());
    }

    [Fact]
    public void Two_features_move_together_only_once_single_moves_run_out()
    {
        // Four single moves exist (two hair colours, one style, one eye colour).
        var variants = AppearanceVariations.For(Declared(), Profile(), count: 6, seed: 7);

        Assert.Equal(6, variants.Count);
        Assert.All(variants.Skip(1).Take(4), v => Assert.Single(v.Changes));
        Assert.Equal(2, variants[5].Changes.Count);
        Assert.NotEqual(variants[5].Changes[0].Feature, variants[5].Changes[1].Feature);
    }

    [Fact]
    public void A_declared_value_outside_the_vocabulary_is_never_varied()
    {
        var unlisted = Declared() with
        {
            HairColor = "green hair",
            HairStyle = "twin drills",
            EyeColor = "heterochromia",
        };

        var variants = AppearanceVariations.For(unlisted, Profile(), count: 4, seed: 7);

        Assert.Single(variants);
        Assert.Equal(unlisted, variants[0].Appearance);
    }
}
