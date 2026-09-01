using System.Text;
using Game.Imaging.Comfy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Game.Imaging.Tests;

/// <summary>
/// Exercises the websocket frame handling against the message shapes ComfyUI documents.
/// This code was written without a live server, so these are the closest thing to proof
/// that the protocol reading is right before the GPU box exists.
/// </summary>
public class ComfyProtocolTests
{
    private const string OurPrompt = "11111111-1111-1111-1111-111111111111";
    private const string OtherPrompt = "22222222-2222-2222-2222-222222222222";

    private static ComfyClient Client() => new(
        new HttpClient { BaseAddress = new Uri("http://gpu-box:8188") },
        Options.Create(new ComfyOptions { BaseAddress = new Uri("http://gpu-box:8188") }),
        NullLogger<ComfyClient>.Instance);

    private static bool Handle(string json, IProgress<ComfyProgress>? progress = null) =>
        Client().HandleMessage(Encoding.UTF8.GetBytes(json), OurPrompt, progress);

    [Fact]
    public void Executing_with_null_node_for_our_prompt_means_complete()
    {
        Assert.True(Handle("""{"type":"executing","data":{"node":null,"prompt_id":"11111111-1111-1111-1111-111111111111"}}"""));
    }

    [Fact]
    public void Executing_a_node_is_not_completion()
    {
        Assert.False(Handle("""{"type":"executing","data":{"node":"6","prompt_id":"11111111-1111-1111-1111-111111111111"}}"""));
    }

    /// <summary>
    /// ComfyUI broadcasts to every connected client. Another session finishing must not be
    /// mistaken for ours, or we would fetch outputs that do not exist yet.
    /// </summary>
    [Fact]
    public void Completion_for_another_prompt_is_ignored()
    {
        Assert.False(Handle("""{"type":"executing","data":{"node":null,"prompt_id":"22222222-2222-2222-2222-222222222222"}}"""));
    }

    /// <summary>
    /// Status frames carry no prompt id at all. Treating a missing id as a match would make
    /// the very first status broadcast look like completion.
    /// </summary>
    [Fact]
    public void Frames_without_a_prompt_id_are_never_completion()
    {
        Assert.False(Handle("""{"type":"status","data":{"status":{"exec_info":{"queue_remaining":1}}}}"""));
        Assert.False(Handle("""{"type":"executing","data":{"node":null}}"""));
    }

    [Fact]
    public void Progress_is_reported_and_is_not_completion()
    {
        ComfyProgress? seen = null;
        var progress = new Progress<ComfyProgress>(p => seen = p);

        var done = Client().HandleMessage(
            Encoding.UTF8.GetBytes(
                """{"type":"progress","data":{"value":7,"max":20,"prompt_id":"11111111-1111-1111-1111-111111111111","node":"3"}}"""),
            OurPrompt,
            // Progress<T> posts to a synchronization context; use a direct sink instead.
            new DirectProgress(p => seen = p));

        Assert.False(done);
        Assert.NotNull(seen);
        Assert.Equal(7, seen!.Value);
        Assert.Equal(20, seen.Max);
        Assert.Equal("3", seen.NodeId);
        Assert.NotNull(progress);
    }

    [Fact]
    public void Execution_error_throws_with_the_traceback_intact()
    {
        var ex = Assert.Throws<ComfyExecutionException>(() => Handle(
            """
            {"type":"execution_error","data":{
              "prompt_id":"11111111-1111-1111-1111-111111111111",
              "node_id":"14",
              "node_type":"IPAdapterModelLoader",
              "exception_type":"FileNotFoundError",
              "exception_message":"ip-adapter-plus_sd15.safetensors not found",
              "traceback":["Traceback (most recent call last):","  File \"nodes.py\", line 1"]
            }}
            """));

        Assert.Equal("14", ex.NodeId);
        Assert.Equal("IPAdapterModelLoader", ex.NodeType);
        Assert.Equal(2, ex.Traceback.Count);
        Assert.Contains("ip-adapter-plus_sd15", ex.Message, StringComparison.Ordinal);
        Assert.Contains("FileNotFoundError", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Execution_error_for_another_prompt_does_not_throw()
    {
        Assert.False(Handle(
            """{"type":"execution_error","data":{"prompt_id":"22222222-2222-2222-2222-222222222222","exception_message":"boom"}}"""));
    }

    [Fact]
    public void Interruption_throws()
    {
        Assert.Throws<ComfyExecutionException>(() => Handle(
            """{"type":"execution_interrupted","data":{"prompt_id":"11111111-1111-1111-1111-111111111111","node_id":"3"}}"""));
    }

    [Fact]
    public void Unknown_and_malformed_frames_are_ignored()
    {
        Assert.False(Handle("""{"type":"execution_cached","data":{"nodes":["4"],"prompt_id":"x"}}"""));
        Assert.False(Handle("""{"type":"b_preview"}"""));
        Assert.False(Handle("not json at all"));
        Assert.False(Handle("[]"));
    }

    [Theory]
    [InlineData("http://gpu-box:8188", "ws://gpu-box:8188/ws")]
    [InlineData("https://gpu-box:8188", "wss://gpu-box:8188/ws")]
    [InlineData("http://gpu-box:8188/", "ws://gpu-box:8188/ws")]
    [InlineData("http://proxy/comfy/", "ws://proxy/comfy/ws")]
    public void Websocket_uri_derives_from_the_base_address(string baseAddress, string expectedPrefix)
    {
        var uri = ComfyClient.BuildWebSocketUri(new Uri(baseAddress), "abc-123");

        Assert.StartsWith(expectedPrefix, uri.ToString(), StringComparison.Ordinal);
        Assert.Contains("clientId=abc-123", uri.Query, StringComparison.Ordinal);
    }

    private sealed class DirectProgress(Action<ComfyProgress> sink) : IProgress<ComfyProgress>
    {
        public void Report(ComfyProgress value) => sink(value);
    }
}
