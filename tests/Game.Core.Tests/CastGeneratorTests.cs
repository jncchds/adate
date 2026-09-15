using Game.Core.Cast;
using Game.Core.Characters;
using Game.Core.Style;

namespace Game.Core.Tests;

public class CastGeneratorTests
{
    private static CastContent Shipped()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.EnumerateFiles("*.sln").Concat(dir.EnumerateFiles("*.slnx")).Any() is false)
        {
            dir = dir.Parent;
        }

        var content = Path.Combine(dir?.FullName ?? throw new InvalidOperationException("No repository root."), "content");
        return CastContent.Load(
            Path.Combine(content, "temper.json"),
            Path.Combine(content, "wants.json"),
            Path.Combine(content, "contrasts.json"));
    }

    private static SubjectProfile Subject() => new(
        ["a single woman"],
        ["man"],
        ["a white blouse"],
        [
            new AgeBand(16, ["a young adult woman"]),
            new AgeBand(18, ["a young adult"]),
            new AgeBand(25, ["an adult"]),
            new AgeBand(40, ["middle-aged"]),
            new AgeBand(60, ["older"]),
        ],
        new Dictionary<string, IReadOnlyList<FeatureOption>>
        {
            [AppearanceFeatures.HairColor] = [new("black hair", ["brown hair"]), new("brown hair", ["black hair"]), new("red hair"), new("blonde hair"), new("silver hair")],
            [AppearanceFeatures.HairStyle] = [new("short hair"), new("a bob"), new("long hair"), new("a ponytail"), new("a braid")],
            [AppearanceFeatures.EyeColor] = [new("brown eyes"), new("green eyes"), new("blue eyes")],
            [AppearanceFeatures.SkinTone] = [new("pale skin"), new("fair skin"), new("tanned skin"), new("brown skin"), new("dark skin")],
            [AppearanceFeatures.Build] = [new("an average build"), new("an athletic build"), new("a petite build", PlayerOnly: true)],
            [AppearanceFeatures.Height] = [new("average height"), new("tall"), new("short", PlayerOnly: true)],
        },
        new Dictionary<string, IReadOnlyList<string>>
        {
            ["sporty"] = ["a track jacket"],
            ["preppy"] = ["a cable-knit sweater"],
            ["alternative"] = ["a band t-shirt"],
            ["rugged"] = ["a denim jacket"],
            ["elegant"] = ["a camel coat"],
            ["artsy"] = ["a paint-flecked shirt"],
        },
        ["glasses", "freckles", "a beauty mark", "a nose stud"]);

    private static CharacterAppearance Declared(int age = 24) => new(
        Subject: "female",
        Age: age,
        EyeColor: "brown eyes",
        HairColor: "black hair",
        HairStyle: "short hair",
        SkinTone: "fair skin",
        Build: "an average build",
        Height: "average height",
        DistinguishingFeature: "");

    private static (CastMember Main, IReadOnlyList<CastMember> Cast) Build(long seed = 1234, int age = 24)
    {
        var content = Shipped();
        var main = CastGenerator.PlaceholderMain(Declared(age), Subject(), content, anchorSeed: seed);
        return (main, CastGenerator.For(main, Subject(), content, seed));
    }

    private static IEnumerable<long> Seeds() => Enumerable.Range(0, 60).Select(i => (long)(i * 7919 + 13));

    /// <summary>The map shows everyone in their everyday outfit, which should show how outgoing they are.</summary>
    [Fact]
    public void Energy_picks_every_members_aesthetic_on_every_seed()
    {
        var energy = Shipped().Axis("energy");
        Assert.True(energy.Dresses);

        foreach (var seed in Seeds())
        {
            var (main, cast) = Build(seed);

            foreach (var member in cast.Prepend(main))
            {
                Assert.Contains(member.Aesthetic, energy.End(member.Temper["energy"]).Aesthetics);
            }
        }
    }

    [Fact]
    public void Only_one_temper_axis_may_pick_the_aesthetic()
    {
        var content = Shipped();
        var twice = content with { Temper = [.. content.Temper.Select(a => a with { Dresses = true })] };

        Assert.Throws<InvalidOperationException>(twice.Validate);
    }

    [Fact]
    public void One_member_per_contrast_profile_in_content_order()
    {
        var (_, cast) = Build();

        Assert.Equal(Shipped().Contrasts.Select(c => c.Id), cast.Select(m => m.ProfileId));
    }

    /// <summary>The cast is rebuilt on request; a different one would miss the cache and swap people.</summary>
    [Fact]
    public void The_same_seed_builds_the_same_cast()
    {
        var (_, first) = Build(seed: 99);
        var (_, second) = Build(seed: 99);

        Assert.Equal(
            first.Select(m => (m.Appearance, m.Aesthetic, m.WantId, m.Seed, string.Join(",", m.Temper.OrderBy(t => t.Key)))),
            second.Select(m => (m.Appearance, m.Aesthetic, m.WantId, m.Seed, string.Join(",", m.Temper.OrderBy(t => t.Key)))));
    }

    [Fact]
    public void Every_member_meets_the_look_budget_on_every_seed()
    {
        foreach (var seed in Seeds())
        {
            foreach (var member in Build(seed).Cast)
            {
                Assert.True(member.LookChanges.Count >= CastContent.MinimumLookChanges, $"seed {seed}, {member.ProfileId}: {member.LookChanges.Count} changes");
                Assert.Contains(member.LookChanges, c => LookDimensions.Silhouette.Contains(c.Dimension));
            }
        }
    }

    [Fact]
    public void Temper_flips_follow_each_profile()
    {
        foreach (var seed in Seeds())
        {
            var (main, cast) = Build(seed);

            Assert.Equal(["temper", "energy"], cast[0].FlippedAxes(main).Order().Reverse());
            Assert.Equal(4, cast[1].FlippedAxes(main).Count);
            Assert.Equal(2, cast[2].FlippedAxes(main).Count);
        }
    }

    [Fact]
    public void Wants_differ_from_everyone_and_follow_each_profile()
    {
        var content = Shipped();

        foreach (var seed in Seeds())
        {
            var (main, cast) = Build(seed);
            var mainWant = content.Want(main.WantId);

            Assert.Equal(4, cast.Select(m => m.WantId).Append(main.WantId).Distinct().Count());

            // Bolder: same family. Every shipped family has at least two wants.
            Assert.Equal(mainWant.Family, content.Want(cast[0].WantId).Family);

            // Opposite: a conflicting want when one exists, otherwise a different family.
            var opposite = content.Want(cast[1].WantId);
            if (mainWant.ConflictsWith.Count > 0)
            {
                Assert.Contains(opposite.Id, mainWant.ConflictsWith);
            }
            else
            {
                Assert.NotEqual(mainWant.Family, opposite.Family);
            }

            // Other life: a different family.
            Assert.NotEqual(mainWant.Family, content.Want(cast[2].WantId).Family);
        }
    }

    [Fact]
    public void Subject_never_changes()
    {
        foreach (var seed in Seeds())
        {
            Assert.All(Build(seed).Cast, m => Assert.Equal("female", m.Appearance.Subject));
        }
    }

    [Theory]
    [InlineData(18)]
    [InlineData(24)]
    [InlineData(45)]
    [InlineData(70)]
    public void An_adult_main_LI_gets_adult_variants_within_ten_years_above(int age)
    {
        foreach (var seed in Seeds())
        {
            var (_, cast) = Build(seed, age);

            Assert.All(cast, m => Assert.InRange(m.Appearance.Age, 18, age + 10));

            var otherLife = cast[2].Appearance.Age;
            Assert.NotEqual(Subject().BandFor(age).From, Subject().BandFor(otherLife).From);

            // A different chapter has to look like one: never a single year across a band boundary.
            Assert.True(Math.Abs(otherLife - age) >= CastGenerator.MinimumAgeGap, $"seed {seed}: {age} -> {otherLife}");
        }
    }

    /// <summary>A generator never produces a minor the player did not describe, at any age.</summary>
    [Fact]
    public void A_main_LI_under_18_passes_their_exact_age_to_every_variant_and_the_budget_still_holds()
    {
        foreach (var seed in Seeds())
        {
            var (_, cast) = Build(seed, age: 16);

            Assert.All(cast, m =>
            {
                Assert.Equal(16, m.Appearance.Age);
                Assert.True(m.LookChanges.Count >= CastContent.MinimumLookChanges);
                Assert.DoesNotContain(m.LookChanges, c => c.Dimension == LookDimensions.Age);
            });
        }
    }

    [Fact]
    public void Player_only_choices_are_never_generated()
    {
        foreach (var seed in Seeds())
        {
            Assert.All(Build(seed).Cast, m =>
            {
                Assert.NotEqual("a petite build", m.Appearance.Build);
                Assert.NotEqual("short", m.Appearance.Height);
            });
        }
    }

    /// <summary>
    /// Two variants with the same new hair colour, hair style or aesthetic blur the comparison.
    /// Hair colour was added after two variants both came out purple-haired on the debug page.
    /// </summary>
    [Fact]
    public void No_two_members_share_a_moved_hair_colour_hair_style_aesthetic_or_age()
    {
        foreach (var seed in Seeds())
        {
            var (main, cast) = Build(seed);

            foreach (var dimension in new[] { LookDimensions.HairColor, LookDimensions.HairStyle, LookDimensions.Aesthetic, LookDimensions.Age })
            {
                var moved = cast.SelectMany(m => m.LookChanges).Where(c => c.Dimension == dimension).Select(c => c.To).ToList();
                Assert.Equal(moved.Count, moved.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            }
        }
    }

    [Fact]
    public void Every_member_has_their_own_seed()
    {
        var (main, cast) = Build();

        Assert.Equal(4, cast.Select(m => m.Seed).Append(main.Seed).Distinct().Count());
        Assert.All(cast, m => Assert.InRange(m.Seed, 0, int.MaxValue));
    }

    [Fact]
    public void A_temper_axis_without_exactly_two_ends_is_refused()
    {
        var content = Shipped();
        var broken = content with
        {
            Temper = [.. content.Temper.Skip(1), content.Temper[0] with { Ends = [.. content.Temper[0].Ends, content.Temper[0].Ends[0] with { Id = "third" }] }],
        };

        var ex = Assert.Throws<InvalidOperationException>(broken.Validate);
        Assert.Contains("exactly two", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_one_sided_conflict_is_refused()
    {
        var content = Shipped();
        var broken = content with
        {
            Wants = [.. content.Wants.Select(w => w.Id == "leave-town" ? w with { ConflictsWith = [] } : w)],
        };

        Assert.Throws<InvalidOperationException>(broken.Validate);
    }

    [Theory]
    [InlineData(new[] { "hairColor", "eyeColor" }, "at least 3")]
    [InlineData(new[] { "hairColor", "eyeColor", "skinTone" }, "silhouette")]
    public void A_profile_that_cannot_meet_the_budget_is_refused(string[] look, string expected)
    {
        var content = Shipped();
        var broken = content with { Contrasts = [content.Contrasts[0] with { Look = look }] };

        var ex = Assert.Throws<InvalidOperationException>(broken.Validate);
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_main_LI_keeps_the_temper_the_player_chose()
    {
        var content = Shipped();
        var chosen = content.Temper.ToDictionary(a => a.Id, a => a.Ends[1].Id);

        var main = CastGenerator.Main(Declared(), chosen, Subject(), content, anchorSeed: 7);

        Assert.Equal(chosen, main.Temper);
        Assert.True(main.IsMain);
        Assert.Contains(content.Wants, w => w.Id == main.WantId);
    }

    [Fact]
    public void A_temper_missing_an_axis_is_refused()
    {
        var content = Shipped();
        var partial = content.Temper.Skip(1).ToDictionary(a => a.Id, a => a.Ends[0].Id);

        var ex = Assert.Throws<ArgumentException>(() => CastGenerator.Main(Declared(), partial, Subject(), content, anchorSeed: 7));

        Assert.Contains(content.Temper[0].Id, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resting_expression_is_the_majority_of_temper_ends()
    {
        var content = Shipped();
        var calm = content.Temper.ToDictionary(a => a.Id, a => a.Ends[0].Id);
        var member = new CastMember(null, Declared(), "", calm, content.Wants[0].Id, 1, []);

        // calm, reserved, guarded, dry rest neutral; easygoing rests smile.
        Assert.Equal("neutral", member.RestingExpression(content));
    }
}
