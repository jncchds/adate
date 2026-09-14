using Game.Core.Content;
using Game.Core.Scenes;
using Game.Core.Style;

namespace Game.Core.Tests;

/// <summary>
/// Loads the content actually shipped in <c>stylepacks/</c> and <c>content/</c>. The unit
/// tests above run against stubs, so nothing else would catch a typo in a real manifest until
/// a generation failed on the GPU box minutes later.
/// </summary>
public class ShippedContentTests
{
    private const string PackId = "counterfeit-anime";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        // Matched by glob rather than by name: .NET 10 writes .slnx, older SDKs write .sln,
        // and this test should not break the day the solution format changes again.
        while (dir is not null &&
               dir.EnumerateFiles("*.sln").Concat(dir.EnumerateFiles("*.slnx")).Any() is false)
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    private static JsonStylePackLoader PackLoader() => new(Path.Combine(RepoRoot(), "stylepacks"));

    private static JsonLocationCatalog Catalog() =>
        new(Path.Combine(RepoRoot(), "content", "place-types.json"));

    private static Settings.JsonSettingCatalog SettingCatalog() =>
        new(Path.Combine(RepoRoot(), "content", "settings"), Catalog());

    [Fact]
    public void The_three_planned_settings_load()
    {
        var settings = SettingCatalog().All();

        Assert.Equal(["big-city", "small-town", "summer-camp"], settings.Select(s => s.Id));
        Assert.All(settings, s => Assert.Equal(Settings.JsonSettingCatalog.OpeningCount, s.Openings.Count));
    }

    /// <summary>A setting's places are its backgrounds. Every one must render at every time, details included.</summary>
    [Fact]
    public async Task Every_place_in_every_setting_compiles_at_every_time_of_day_in_both_dialects()
    {
        var zimage = await PackLoader().LoadAsync(ZImagePackId);
        var booru = await PackLoader().LoadAsync(PackId);
        var catalog = Catalog();
        var natural = new NaturalPromptCompiler(catalog);
        var tags = new BooruPromptCompiler(catalog);

        foreach (var setting in SettingCatalog().All())
        {
            foreach (var place in setting.Places)
            {
                foreach (var time in Enum.GetValues<TimeOfDay>())
                {
                    var intent = new SceneIntent(place.Type, time, "", "", "", Framing.FullBody, place.Details);

                    var prose = natural.CompilePositive(null, Approve(zimage, intent), zimage, RenderTarget.Background);
                    var booruPrompt = tags.CompilePositive(null, Approve(booru, intent), booru, RenderTarget.Background);

                    foreach (var detail in place.Details ?? [])
                    {
                        Assert.Contains(catalog.Get(place.Type).Detail(detail).Phrase, prose, StringComparison.Ordinal);
                    }

                    Assert.Contains("no people", prose, StringComparison.Ordinal);
                    Assert.Contains("no humans", booruPrompt, StringComparison.Ordinal);
                }
            }
        }

        static Content.ApprovedIntent Approve(StylePack pack, SceneIntent intent) =>
            Content.ApprovedIntent.Approve(
                Content.ContentPolicy.Resolve(Content.GameContentSettings.SafeDefault, 24, pack.HighestCeiling, Content.Intimacy.None),
                intent,
                pack);
    }

    /// <summary>
    /// Measured on Z-Image: a background whose description implies people gets them, whatever the
    /// "no people" sentence says. "Rows of food stalls" drew vendors in 5 of 5 renders. "Empty,
    /// unattended stalls, set up but not yet open" drew none in 6. A background is cached for the
    /// life of a save, so the wording is guarded here.
    /// </summary>
    [Fact]
    public void No_place_type_describes_people()
    {
        string[] people =
        [
            "people", "person", "crowd", "customer", "vendor", "shopper", "pedestrian", "patron",
            "visitor", "tourist", "camper", "worker", "children", "kids", "passerby", "passers-by",
            "bustling", "busy",
        ];

        foreach (var type in Catalog().All())
        {
            IEnumerable<string> wording = [type.Description!, .. type.TimeDescriptions!.Values, .. type.Details!.Select(d => d.Phrase)];

            foreach (var text in wording)
            {
                foreach (var word in people)
                {
                    Assert.False(
                        text.Contains(word, StringComparison.OrdinalIgnoreCase),
                        $"place type '{type.Id}': \"{text}\" mentions '{word}'");
                }
            }
        }
    }

    [Fact]
    public void Every_place_type_offers_details()
    {
        Assert.All(Catalog().All(), type => Assert.True((type.Details?.Count ?? 0) >= 2, $"place type '{type.Id}' offers too few details"));
    }

    [Fact]
    public async Task The_shipped_pack_loads()
    {
        var pack = await PackLoader().LoadAsync(PackId);

        Assert.Equal(PackId, pack.Id);
        Assert.Equal(PromptDialect.Booru, pack.Dialect);

        // HANDOFF 2: FaceID and InstantID rely on InsightFace embeddings trained on real
        // faces and perform poorly on anime. An anime pack selecting one is a real mistake.
        Assert.Equal(ConsistencyStrategy.IpAdapterPlus, pack.Consistency);
    }

    [Fact]
    public async Task The_shipped_pack_declares_a_ceiling_and_honours_it()
    {
        var pack = await PackLoader().LoadAsync(PackId);

        Assert.NotEmpty(pack.SupportedCeilings);
        Assert.True(pack.Supports(Ceiling.PG13));
        Assert.False(pack.Supports(Ceiling.Explicit));
    }

    /// <summary>
    /// SD1.5 was trained at 512px and degrades into duplicated limbs and torsos well before
    /// 1024. These resolutions are the pack's guard against that, so they are worth asserting.
    /// </summary>
    [Fact]
    public async Task The_shipped_pack_stays_within_its_base_model_resolution()
    {
        var pack = await PackLoader().LoadAsync(PackId);

        Assert.Equal("sd15", pack.BaseModel);

        foreach (var size in new[]
                 {
                     pack.Resolutions.Portrait,
                     pack.Resolutions.Sprite,
                     pack.Resolutions.Background,
                 })
        {
            Assert.InRange(size.Width, 384, 1024);
            Assert.InRange(size.Height, 384, 1024);

            // Latent dimensions are the pixel size divided by 8; anything not a multiple of
            // 64 is silently rounded by the sampler and quietly stops matching the cache key.
            Assert.Equal(0, size.Width % 64);
            Assert.Equal(0, size.Height % 64);
        }
    }

    [Fact]
    public async Task Every_shipped_location_compiles_at_every_time_of_day()
    {
        var pack = await PackLoader().LoadAsync(PackId);
        var catalog = Catalog();
        var compiler = new BooruPromptCompiler(catalog);

        Assert.NotEmpty(catalog.All());

        foreach (var location in catalog.All())
        {
            foreach (var time in Enum.GetValues<TimeOfDay>())
            {
                var intent = new SceneIntent(location.Id, time, "sweater", "standing", "smile", Framing.Bust);
                var prompt = compiler.CompilePositive(
                    appearance: null,
                    ApprovedIntent.Approve(
                        ContentPolicy.Resolve(GameContentSettings.SafeDefault, 24, pack.HighestCeiling, Intimacy.None),
                        intent,
                        pack),
                    pack,
                    RenderTarget.Background);

                Assert.Contains("no humans", prompt, StringComparison.Ordinal);

                // A location missing a time entry would compile to an ambiguously lit scene
                // rather than failing, so assert the time actually contributed something.
                Assert.True(
                    location.TimeTags.ContainsKey(time.ToString()),
                    $"location '{location.Id}' has no tags for {time}");
            }
        }
    }

    private const string ZImagePackId = "zimage-anime";

    [Fact]
    public async Task The_zimage_pack_loads_as_a_natural_seed_and_prompt_pack()
    {
        var pack = await PackLoader().LoadAsync(ZImagePackId);

        Assert.Equal(PromptDialect.Natural, pack.Dialect);

        // Z-Image refuses a pose skeleton, and SeedAndTags always uploads one.
        Assert.Equal(ConsistencyStrategy.SeedAndPrompt, pack.Consistency);

        // Z-Image Turbo runs at cfg 1.0, where negatives are never applied, and the provider refuses one.
        Assert.Equal(NegativeSupport.Ignored, pack.NegativePrompts);
        Assert.NotEmpty(pack.AlwaysNegative); // still the vocabulary guard

        // The Z-Image provider mattes the workflows it is configured to, and its default is this one.
        Assert.Equal("zimage-sprite", pack.Workflows.Sprite);
    }

    /// <summary>Z-Image's DiT patchifies the 8x latent in 2x2 blocks, so every side must divide by 16.</summary>
    [Fact]
    public async Task The_zimage_pack_resolutions_divide_by_16()
    {
        var pack = await PackLoader().LoadAsync(ZImagePackId);

        foreach (var size in new[] { pack.Resolutions.Portrait, pack.Resolutions.Sprite, pack.Resolutions.Background })
        {
            Assert.Equal(0, size.Width % 16);
            Assert.Equal(0, size.Height % 16);
        }
    }

    [Fact]
    public async Task Every_shipped_location_describes_itself_at_every_time_of_day()
    {
        var pack = await PackLoader().LoadAsync(ZImagePackId);
        var catalog = Catalog();
        var compiler = new NaturalPromptCompiler(catalog);

        foreach (var location in catalog.All())
        {
            foreach (var time in Enum.GetValues<TimeOfDay>())
            {
                var prompt = compiler.CompilePositive(
                    appearance: null,
                    ApprovedIntent.Approve(
                        ContentPolicy.Resolve(GameContentSettings.SafeDefault, 24, pack.HighestCeiling, Intimacy.None),
                        new SceneIntent(location.Id, time, "", "", "", Framing.FullBody),
                        pack),
                    pack,
                    RenderTarget.Background);

                Assert.Contains("no people", prompt, StringComparison.Ordinal);
                Assert.True(
                    location.TimeDescriptions?.ContainsKey(time.ToString()) is true,
                    $"location '{location.Id}' has no description for {time}");
            }
        }
    }

    [Theory]
    [InlineData("female")]
    [InlineData("male")]
    public async Task The_zimage_pack_compiles_a_sprite_for_each_subject(string subject)
    {
        var pack = await PackLoader().LoadAsync(ZImagePackId);
        var profile = pack.SubjectFor(subject);

        string First(string feature) => profile.OptionsFor(feature)[0].Tag;

        var appearance = new Characters.CharacterAppearance(
            subject, 24,
            EyeColor: First(Characters.AppearanceFeatures.EyeColor),
            HairColor: First(Characters.AppearanceFeatures.HairColor),
            HairStyle: First(Characters.AppearanceFeatures.HairStyle),
            SkinTone: First(Characters.AppearanceFeatures.SkinTone),
            Build: First(Characters.AppearanceFeatures.Build),
            Height: First(Characters.AppearanceFeatures.Height),
            DistinguishingFeature: "");

        var intent = new SceneIntent("studio", TimeOfDay.Midday, string.Join(", ", profile.Outfit), "standing", pack.ExpressionFor("smile"), Framing.HalfBody);
        var prompt = new NaturalPromptCompiler(Catalog()).CompilePositive(
            appearance,
            ApprovedIntent.Approve(ContentPolicy.Resolve(GameContentSettings.SafeDefault, 24, pack.HighestCeiling, Intimacy.None), intent, pack),
            pack,
            RenderTarget.Sprite);

        Assert.Contains(profile.Positive[0], prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(profile.BandFor(24).Tags[0], prompt, StringComparison.Ordinal);
        Assert.Contains("Only this one person", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_shipped_cast_content_loads_and_fits_the_zimage_pack()
    {
        var content = Cast.CastContent.Load(
            Path.Combine(RepoRoot(), "content", "temper.json"),
            Path.Combine(RepoRoot(), "content", "wants.json"),
            Path.Combine(RepoRoot(), "content", "contrasts.json"));

        content.ValidateAgainst(await PackLoader().LoadAsync(ZImagePackId));

        Assert.Equal(["bolder", "opposite", "other-life"], content.Contrasts.Select(c => c.Id));
    }

    [Theory]
    [InlineData("female")]
    [InlineData("male")]
    public async Task The_zimage_pack_builds_a_full_cast_for_each_subject(string subject)
    {
        var pack = await PackLoader().LoadAsync(ZImagePackId);
        var content = Cast.CastContent.Load(
            Path.Combine(RepoRoot(), "content", "temper.json"),
            Path.Combine(RepoRoot(), "content", "wants.json"),
            Path.Combine(RepoRoot(), "content", "contrasts.json"));
        var profile = pack.SubjectFor(subject);

        string First(string feature) => profile.OptionsFor(feature)[0].Tag;

        var appearance = new Characters.CharacterAppearance(
            subject, 24,
            EyeColor: First(Characters.AppearanceFeatures.EyeColor),
            HairColor: First(Characters.AppearanceFeatures.HairColor),
            HairStyle: First(Characters.AppearanceFeatures.HairStyle),
            SkinTone: First(Characters.AppearanceFeatures.SkinTone),
            Build: First(Characters.AppearanceFeatures.Build),
            Height: First(Characters.AppearanceFeatures.Height),
            DistinguishingFeature: "");

        for (var seed = 0L; seed < 40; seed++)
        {
            var main = Cast.CastGenerator.PlaceholderMain(appearance, profile, content, seed);
            var cast = Cast.CastGenerator.For(main, profile, content, seed + 1000);

            Assert.Equal(3, cast.Count);
            Assert.All(cast, m => Assert.False(string.IsNullOrEmpty(m.Aesthetic)));
            Assert.All(cast, m => Assert.NotEmpty(profile.AestheticOutfit(m.Aesthetic)));
            Assert.All(cast, m => Assert.NotEmpty(pack.ExpressionFor(m.RestingExpression(content))));
        }
    }

    [Fact]
    public void An_unknown_location_names_the_ones_that_exist()
    {
        var ex = Assert.Throws<KeyNotFoundException>(() => Catalog().Get("atlantis"));

        Assert.Contains("cafe", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_pack_list_finds_the_shipped_pack()
    {
        Assert.Contains(PackId, await PackLoader().ListAsync());
    }

    /// <summary>
    /// Every shipped pack, not just the one this class otherwise focuses on. A band gap is
    /// invisible until a character of that age is created, which could be long after release.
    /// </summary>
    [Fact]
    public async Task Every_shipped_pack_covers_every_permitted_age_with_every_subject()
    {
        var loader = PackLoader();

        foreach (var packId in await loader.ListAsync())
        {
            var pack = await loader.LoadAsync(packId);

            foreach (var (subject, profile) in pack.Subjects)
            {
                for (var age = Characters.CharacterAppearance.MinimumAge; age <= 99; age++)
                {
                    var band = profile.BandFor(age);
                    Assert.True(
                        band.Tags.Count > 0,
                        $"pack {packId}, subject {subject}, age {age}: band has no tags");
                }
            }
        }
    }

    /// <summary>
    /// The maturity anchor lives in the age band, not in the subject. Carrying it in both
    /// would emit it twice at two different weights, and the heavier one would silently win.
    /// </summary>
    [Fact]
    public async Task No_shipped_pack_carries_a_maturity_anchor_in_both_places()
    {
        var loader = PackLoader();

        foreach (var packId in await loader.ListAsync())
        {
            var pack = await loader.LoadAsync(packId);

            foreach (var (subject, profile) in pack.Subjects)
            {
                foreach (var tag in profile.Positive)
                {
                    Assert.False(
                        tag.Contains("mature", StringComparison.OrdinalIgnoreCase) ||
                        tag.Equals("adult", StringComparison.OrdinalIgnoreCase),
                        $"pack {packId}, subject {subject}: {tag} belongs in an age band, not in the subject");
                }
            }
        }
    }

    /// <summary>
    /// Appearance is picked from pack vocabulary, so the vocabulary is the player's whole input
    /// surface. HANDOFF 1.9 names these as what tag checkpoints read as juvenile; none may be a
    /// choice, whether declared or offered as an alternative.
    /// </summary>
    [Fact]
    public async Task No_shipped_appearance_choice_is_juvenile_coded()
    {
        string[] juvenile = ["petite", "slim", "flat chest", "youthful", "young", "child", "loli", "shota", "teen", "baby face"];
        var loader = PackLoader();

        foreach (var packId in await loader.ListAsync())
        {
            var pack = await loader.LoadAsync(packId);

            foreach (var (subject, profile) in pack.Subjects)
            {
                foreach (var feature in Characters.AppearanceFeatures.All)
                {
                    foreach (var option in profile.OptionsFor(feature))
                    {
                        foreach (var term in juvenile)
                        {
                            Assert.False(
                                option.Tag.Contains(term, StringComparison.OrdinalIgnoreCase),
                                $"pack {packId}, subject {subject}, {feature}: '{option.Tag}' contains '{term}'");

                            // The prompt wording is what reaches the generator.
                            Assert.False(
                                option.Prompt?.Contains(term, StringComparison.OrdinalIgnoreCase) is true,
                                $"pack {packId}, subject {subject}, {feature}: prompt '{option.Prompt}' contains '{term}'");
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// The form defaults to each subject's first choices. If those had too few neighbours, a new
    /// player would be offered fewer portraits than the page promises.
    /// </summary>
    [Fact]
    public async Task Every_shipped_subject_offers_four_looks_from_its_first_choices()
    {
        var loader = PackLoader();

        foreach (var packId in await loader.ListAsync())
        {
            var pack = await loader.LoadAsync(packId);

            foreach (var (subject, profile) in pack.Subjects)
            {
                string First(string feature) => profile.OptionsFor(feature)[0].Tag;

                var declared = new Characters.CharacterAppearance(
                    subject,
                    24,
                    EyeColor: First(Characters.AppearanceFeatures.EyeColor),
                    HairColor: First(Characters.AppearanceFeatures.HairColor),
                    HairStyle: First(Characters.AppearanceFeatures.HairStyle),
                    SkinTone: First(Characters.AppearanceFeatures.SkinTone),
                    Build: First(Characters.AppearanceFeatures.Build),
                    Height: First(Characters.AppearanceFeatures.Height),
                    DistinguishingFeature: "");

                var variants = Characters.AppearanceVariations.For(declared, profile, count: 4, seed: 1);

                Assert.True(variants.Count == 4, $"pack {packId}, subject {subject}: only {variants.Count} looks");
            }
        }
    }
}
