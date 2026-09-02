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
        new(Path.Combine(RepoRoot(), "content", "locations.json"));

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
                var prompt = compiler.CompilePositive(appearance: null, intent, pack, RenderTarget.Background);

                Assert.Contains("no humans", prompt, StringComparison.Ordinal);

                // A location missing a time entry would compile to an ambiguously lit scene
                // rather than failing, so assert the time actually contributed something.
                Assert.True(
                    location.TimeTags.ContainsKey(time.ToString()),
                    $"location '{location.Id}' has no tags for {time}");
            }
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
}
