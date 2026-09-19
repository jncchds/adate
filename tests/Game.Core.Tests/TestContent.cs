using Game.Core.Characters;
using Game.Core.Content;
using Game.Core.Scenes;
using Game.Core.Style;

namespace Game.Core.Tests;

internal static class TestContent
{
    public static CharacterAppearance Appearance(string subject = "female") => new(
        Subject: subject,
        Age: 24,
        EyeColor: "green eyes",
        HairColor: "red hair",
        HairStyle: "long hair",
        SkinTone: "pale skin",
        Build: "slim",
        Height: "tall",
        DistinguishingFeature: "freckles");

    public static SceneIntent Intent() => new(
        LocationId: "cafe",
        Time: TimeOfDay.Evening,
        Outfit: "sweater",
        Pose: "standing",
        Expression: "smile",
        Framing: Framing.Bust);

    public static StylePack Pack() => new()
    {
        Id = "test-pack",
        DisplayName = "Test",
        Checkpoint = "test.safetensors",
        BaseModel = "sd15",
        Dialect = PromptDialect.Booru,
        Consistency = ConsistencyStrategy.IpAdapterPlus,
        Sampler = new SamplerSettings("dpmpp_2m", "karras", 28, 7.0, 0.75, 0.95),
        Workflows = new WorkflowSet("portrait", "sprite", "background"),
        Resolutions = new ResolutionSet(
            new Size(512, 768),
            new Size(640, 960),
            new Size(768, 512)),
        MattingModel = "birefnet.safetensors",
        SupportedCeilings = [Ceiling.PG13, Ceiling.Suggestive],
        PositivePrefix = ["masterpiece", "best quality"],
        NegativeBase = ["lowres", "worst quality"],
        AlwaysNegative = ["loli", "shota", "child"],
        Subjects = new Dictionary<string, SubjectProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["female"] = new(
                ["1girl", "solo", "adult", "mature female"],
                ["1boy"],
                ["white blouse", "pleated skirt"],
                [new AgeBand(16, ["young adult"]), new AgeBand(18, ["(mature female:1.3)"]), new AgeBand(60, ["(old woman:1.4)", "wrinkles"])]),
            ["male"] = new(
                ["1boy", "solo", "adult", "male focus", "mature male"],
                ["1girl", "feminine"],
                ["white dress shirt", "black trousers"],
                [new AgeBand(16, ["young adult"]), new AgeBand(18, ["(mature male:1.3)"]), new AgeBand(60, ["(old man:1.4)", "wrinkles"])]),
        },
        Expressions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["neutral"] = "neutral expression",
            ["smile"] = "(smile:1.2), happy",
        },
        RestrictedPositive =
        [
            new RestrictedTerms(Ceiling.Suggestive, ["lingerie", "see-through"]),
            new RestrictedTerms(Ceiling.Explicit, ["nude", "topless", "nipples"]),
        ],
        NegativeByCeiling = new Dictionary<string, IReadOnlyList<string>>
        {
            ["PG13"] = ["nsfw", "nude"],
        },
    };

    /// <summary>The same pack as <see cref="Pack"/>, in the natural-language dialect.</summary>
    public static StylePack NaturalPack() => Pack() with
    {
        Id = "test-natural",
        Dialect = PromptDialect.Natural,
        Consistency = ConsistencyStrategy.SeedAndPrompt,
        PositivePrefix = ["Anime illustration", "soft cel shading"],
        NegativeBase = ["low quality", "blurry"],
        Subjects = new Dictionary<string, SubjectProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["female"] = new(
                ["a single woman"],
                ["man"],
                ["a white blouse", "a pleated skirt"],
                [new AgeBand(16, ["a young adult woman"]), new AgeBand(18, ["an adult woman", "adult proportions"]), new AgeBand(60, ["an older woman", "visible wrinkles"])]),
            ["male"] = new(
                ["a single man"],
                ["woman"],
                ["a white shirt", "black trousers"],
                [new AgeBand(16, ["a young adult man"]), new AgeBand(18, ["an adult man", "adult proportions"]), new AgeBand(60, ["an older man", "visible wrinkles"])]),
        },
        Expressions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["neutral"] = "a calm, neutral facial expression",
            ["smile"] = "a warm, gentle smile",
        },
    };

    /// <summary>An intent through the gate at <paramref name="ceiling"/>, for compiler tests.</summary>
    public static ApprovedIntent Approved(SceneIntent? intent = null, Ceiling ceiling = Ceiling.PG13) =>
        ApprovedIntent.Approve(
            ContentPolicy.Resolve(new GameContentSettings(18, ceiling), 24, ceiling, Intimacy.None),
            intent ?? Intent(),
            Pack());

    public static ILocationCatalog Locations() => new StubCatalog();

    private sealed class StubCatalog : ILocationCatalog
    {
        private static readonly LocationDefinition Cafe = new(
            "cafe",
            "Corner cafe",
            ["cafe interior", "wooden tables"],
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["Evening"] = ["golden hour", "warm lamplight"],
            },
            "The interior of a cozy corner cafe with wooden tables",
            new Dictionary<string, string>
            {
                ["Evening"] = "Golden sunset light and warm lamplight",
            },
            [new PlaceDetail("window-seat", ["window seat"], "a sunny window seat")]);

        /// <summary>A location written only for booru packs: tags, no description.</summary>
        private static readonly LocationDefinition Bare = new(
            "bare",
            "Tag-only location",
            ["alley"],
            new Dictionary<string, IReadOnlyList<string>>());

        /// <summary>Somewhere people live, so a setting can have a home for the player to go to.</summary>
        private static readonly LocationDefinition Flat = new(
            "flat",
            "A small flat",
            ["apartment interior"],
            new Dictionary<string, IReadOnlyList<string>>(),
            Dress: DressCode.Home);

        public LocationDefinition Get(string locationId) => locationId switch
        {
            "cafe" => Cafe,
            "bare" => Bare,
            "flat" => Flat,
            _ => throw new KeyNotFoundException(locationId),
        };

        public IReadOnlyList<LocationDefinition> All() => [Bare, Cafe, Flat];
    }
}
