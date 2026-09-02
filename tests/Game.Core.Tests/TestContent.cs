using Game.Core.Characters;
using Game.Core.Content;
using Game.Core.Scenes;
using Game.Core.Style;

namespace Game.Core.Tests;

internal static class TestContent
{
    public static CharacterAppearance Appearance() => new(
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
        Sampler = new SamplerSettings("dpmpp_2m", "karras", 28, 7.0, 0.75),
        Resolutions = new ResolutionSet(
            new Size(512, 768),
            new Size(640, 960),
            new Size(768, 512)),
        MattingModel = "birefnet.safetensors",
        SupportedCeilings = [Ceiling.PG13],
        PositivePrefix = ["masterpiece", "best quality"],
        NegativeBase = ["lowres", "worst quality"],
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
