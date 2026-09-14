using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Game.Core.Gpu;
using Game.Core.Story;
using Microsoft.Extensions.Options;

namespace Game.Llm.Tests;

public class MemoryTests
{
    private sealed class Handler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? Sent { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Lease : IGpuLease
    {
        public List<GpuConsumer> Acquired { get; } = [];

        public Task<IAsyncDisposable> AcquireAsync(GpuConsumer consumer, CancellationToken ct = default)
        {
            Acquired.Add(consumer);
            return Task.FromResult<IAsyncDisposable>(new Nothing());
        }

        private sealed class Nothing : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeLlm(Func<string> answer) : ILlmClient
    {
        public Task<string> CompleteJsonAsync(LlmRequest request, CancellationToken ct = default) => Task.FromResult(answer());
    }

    [Fact]
    public async Task The_embedding_client_sends_the_model_and_reads_the_vector_under_the_embeddings_lease()
    {
        var handler = new Handler(HttpStatusCode.OK, """{ "data": [ { "embedding": [0.5, -0.25, 1] } ] }""");
        var lease = new Lease();
        var client = new OpenAiCompatibleEmbeddingClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://llm.local/v1/") },
            lease,
            Options.Create(new LlmOptions { EmbeddingModel = "text-embedding-nomic-embed-text-v1.5" }));

        var vector = await client.EmbedAsync("Rin laughed at the flyer.");

        Assert.Equal([0.5f, -0.25f, 1f], vector);
        Assert.Equal([GpuConsumer.Embeddings], lease.Acquired);
        Assert.Equal("text-embedding-nomic-embed-text-v1.5", JsonNode.Parse(handler.Sent!)!["model"]!.GetValue<string>());
    }

    private static MemoryEntry Entry(int day, string summary) => new(0, MemoryScope.Scene, day, summary, ["rin"], [], null);

    [Fact]
    public async Task The_compactor_uses_the_models_summary_and_joins_the_originals_when_it_cannot()
    {
        MemoryEntry[] members = [Entry(3, "Coffee with Rin."), Entry(3, "A walk by the river.")];

        Assert.Equal("Rin and you had coffee, then walked.",
            await new MemoryCompactor(new FakeLlm(() => """{ "summary": "Rin and you had coffee, then walked." }""")).SummariseAsync(members, useModel: true));

        Assert.Equal("Coffee with Rin. A walk by the river.",
            await new MemoryCompactor(new FakeLlm(() => throw new HttpRequestException("down"))).SummariseAsync(members, useModel: true));

        Assert.Equal("Coffee with Rin. A walk by the river.",
            await new MemoryCompactor(new FakeLlm(() => "{}")).SummariseAsync(members, useModel: false));
    }
}
