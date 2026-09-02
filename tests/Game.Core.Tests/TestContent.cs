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
        SupportedCeilings = [Ceiling.PG13],
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
        NegativeByCeiling = new Dictionary<string, IReadOnlyList<string>>
        {
            ["PG13"] = ["nsfw", "nude"],
        },
    };

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
            });

        public LocationDefinition Get(string locationId) =>
            locationId == "cafe" ? Cafe : throw new KeyNotFoundException(locationId);

        public IReadOnlyList<LocationDefinition> All() => [Cafe];
    }
}
