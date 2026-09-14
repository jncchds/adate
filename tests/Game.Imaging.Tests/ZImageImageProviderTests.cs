using System.Net;
using System.Text;
using System.Text.Json;
using Game.Core;
using Game.Core.Gpu;
using Game.Imaging.Caching;
using Game.Imaging.ZImage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Game.Imaging.Tests;

/// <summary>
/// The Z-Image provider against a stub of the API shapes confirmed on the GPU box: <c>POST
/// /generate</c> returns an image id, <c>GET /images/{id}</c> returns the PNG.
/// </summary>
public class ZImageImageProviderTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private static ImageRequest Request() => new(
        WorkflowId: "background",
        Positive: "a cozy cafe interior, no people",
        Negative: "",
        Seed: 42,
        Width: 768,
        Height: 768,
        PackFingerprint: "pack",
        AnchorImageHash: null,
        AnchorWeight: null,
        PoseImageHash: null,
        PoseStrength: null,
        Ceiling: Ceiling.PG13);

    private static (ZImageImageProvider Provider, StubHandler Handler, MemoryStore Store) Create(
        ZImageOptions? options = null,
        Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var handler = new StubHandler(respond ?? HappyPath);
        var store = new MemoryStore();
        var provider = new ZImageImageProvider(
            new StubFactory(handler),
            Options.Create(options ?? new ZImageOptions()),
            store,
            new NoOpGpuLease(),
            NullLogger<ZImageImageProvider>.Instance);

        return (provider, handler, store);
    }

    private static HttpResponseMessage HappyPath(HttpRequestMessage request) =>
        request.RequestUri!.AbsolutePath switch
        {
            "/generate" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"image_id":"abc-123","timestamp":"2026-09-13T14:40:00","character_id":null,"prompt":"x","image_path":"/images/abc-123","seed":42}""",
                    Encoding.UTF8,
                    "application/json"),
            },
            "/images/abc-123" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };

    [Fact]
    public async Task Generates_downloads_and_stores_the_png()
    {
        var (provider, handler, store) = Create();

        var image = await provider.GenerateAsync(Request());

        Assert.False(image.FromCache);
        Assert.Equal(Png, store.Files[image.Hash]);
        Assert.Equal(new[] { "POST /generate", "GET /images/abc-123" }, handler.Calls);
    }

    [Fact]
    public async Task Sends_the_request_in_the_api_field_names()
    {
        var (provider, handler, _) = Create(new ZImageOptions { Steps = 12, Cfg = 1.5 });

        await provider.GenerateAsync(Request() with { Negative = "blurry" });

        using var body = JsonDocument.Parse(handler.Bodies[0]);
        var root = body.RootElement;
        Assert.Equal("a cozy cafe interior, no people", root.GetProperty("prompt").GetString());
        Assert.Equal("blurry", root.GetProperty("negative_prompt").GetString());
        Assert.Equal(42, root.GetProperty("seed").GetInt64());
        Assert.Equal(12, root.GetProperty("num_inference_steps").GetInt32());
        Assert.Equal(1.5, root.GetProperty("cfg_scale").GetDouble());
        Assert.Equal(768, root.GetProperty("width").GetInt32());
        Assert.Equal(768, root.GetProperty("height").GetInt32());
        Assert.Equal("keep", root.GetProperty("background").GetString());
    }

    [Fact]
    public async Task A_matte_workflow_asks_the_server_to_remove_the_background()
    {
        var (provider, handler, _) = Create();

        await provider.GenerateAsync(Request() with { WorkflowId = "zimage-sprite" });

        using var body = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("remove", body.RootElement.GetProperty("background").GetString());
    }

    /// <summary>
    /// A sprite and a background from the same prompt and seed are different files, and a sprite
    /// cut out by a different model is a different sprite. A background is not: its address must
    /// not move when only the matting model does, or every cached background would regenerate.
    /// </summary>
    [Fact]
    public void Matting_changes_the_address_only_for_matted_workflows()
    {
        var sprite = Request() with { WorkflowId = "zimage-sprite" };
        var background = Request();
        var birefnet = new ZImageOptions();
        var other = new ZImageOptions { MattingModel = "other-matting-model" };

        Assert.NotEqual(
            ContentAddress.For(sprite, ZImageImageProvider.Fingerprint(birefnet, sprite.WorkflowId)),
            ContentAddress.For(sprite, ZImageImageProvider.Fingerprint(other, sprite.WorkflowId)));
        Assert.Equal(
            ContentAddress.For(background, ZImageImageProvider.Fingerprint(birefnet, background.WorkflowId)),
            ContentAddress.For(background, ZImageImageProvider.Fingerprint(other, background.WorkflowId)));
    }

    [Fact]
    public async Task A_second_identical_request_is_served_from_cache_without_calling_the_api()
    {
        var (provider, handler, _) = Create();

        var first = await provider.GenerateAsync(Request());
        handler.Calls.Clear();
        var second = await provider.GenerateAsync(Request());

        Assert.True(second.FromCache);
        Assert.Equal(first.Hash, second.Hash);
        Assert.Empty(handler.Calls);
    }

    /// <summary>
    /// Steps and cfg live in options, not on the request, so they only reach the address
    /// through the fingerprint. Without it, changing either would keep serving the old image.
    /// </summary>
    [Fact]
    public void Server_side_sampling_settings_change_the_address()
    {
        var eight = ContentAddress.For(Request(), ZImageImageProvider.Fingerprint(new ZImageOptions { Steps = 8 }, "background"));
        var twelve = ContentAddress.For(Request(), ZImageImageProvider.Fingerprint(new ZImageOptions { Steps = 12 }, "background"));
        var cfg = ContentAddress.For(Request(), ZImageImageProvider.Fingerprint(new ZImageOptions { Cfg = 2.0 }, "background"));

        Assert.NotEqual(eight, twelve);
        Assert.NotEqual(eight, cfg);
    }

    [Theory]
    [InlineData("pose")]
    [InlineData("anchor")]
    public async Task A_request_it_cannot_honour_is_refused_before_anything_is_called(string input)
    {
        var (provider, handler, store) = Create();
        var request = input == "pose"
            ? Request() with { PoseImageHash = "skeleton", PoseStrength = 0.95 }
            : Request() with { AnchorImageHash = "anchor", AnchorWeight = 0.8 };

        await Assert.ThrowsAsync<NotSupportedException>(() => provider.GenerateAsync(request));

        Assert.Empty(handler.Calls);
        Assert.Empty(store.Files);
    }

    /// <summary>
    /// Measured: at cfg 1.0 a negative renders byte-identical to none. Accepting one would cache
    /// under an address that differs from the no-negative render while holding the same image.
    /// </summary>
    [Fact]
    public async Task A_negative_prompt_at_cfg_1_is_refused_before_anything_is_called()
    {
        var (provider, handler, store) = Create(new ZImageOptions { Cfg = 1.0 });

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            provider.GenerateAsync(Request() with { Negative = "child, loli" }));

        Assert.Empty(handler.Calls);
        Assert.Empty(store.Files);
    }

    [Fact]
    public async Task A_server_failure_surfaces_the_api_detail_and_stores_nothing()
    {
        var (provider, _, store) = Create(respond: _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("""{"detail":"Generation failed: Failed to find C compiler."}"""),
        });

        var ex = await Assert.ThrowsAsync<ZImageException>(() => provider.GenerateAsync(Request()));

        Assert.Contains("Failed to find C compiler", ex.Message, StringComparison.Ordinal);
        Assert.Contains("500", ex.Message, StringComparison.Ordinal);
        Assert.Empty(store.Files);
    }

    [Fact]
    public async Task A_body_that_is_not_a_png_is_never_cached()
    {
        var (provider, _, store) = Create(respond: request => request.RequestUri!.AbsolutePath.StartsWith("/images/", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>proxy error</html>") }
            : HappyPath(request));

        await Assert.ThrowsAsync<ZImageException>(() => provider.GenerateAsync(Request()));

        Assert.Empty(store.Files);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            if (request.Content is not null)
            {
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }

            return respond(request);
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://gpu-box:8000/") };
    }

    private sealed class MemoryStore : IImageStore
    {
        public Dictionary<string, byte[]> Files { get; } = [];

        public bool Exists(string hash) => Files.ContainsKey(hash);

        public string RelativePath(string hash) => $"img/{hash}.png";

        public Task<byte[]> ReadAsync(string hash, CancellationToken ct = default) => Task.FromResult(Files[hash]);

        public Task SaveAsync(string hash, ReadOnlyMemory<byte> content, CancellationToken ct = default)
        {
            Files[hash] = content.ToArray();
            return Task.CompletedTask;
        }
    }
}
