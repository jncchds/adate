using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Game.Core.Gpu;
using Game.Llm.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Game.Llm.Tests;

public class ProviderTests
{
    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string? Sent { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Sent = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class CountingLease : IGpuLease
    {
        public List<GpuConsumer> Acquired { get; } = [];

        public Task<IAsyncDisposable> AcquireAsync(GpuConsumer consumer, CancellationToken ct = default)
        {
            Acquired.Add(consumer);
            return Task.FromResult<IAsyncDisposable>(new Released());
        }

        private sealed class Released : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static HttpClient Http(RecordingHandler handler, LlmOptions options) => new(handler) { BaseAddress = options.ChatAddress };

    private static LlmRequest Request(int? maxTokens = 700) =>
        new("system", "user", "scene", new JsonObject { ["type"] = "object" }, maxTokens);

    [Fact]
    public void An_empty_address_is_the_providers_default()
    {
        Assert.Equal("https://api.openai.com/v1/", new LlmOptions { Provider = LlmProviderType.OpenAi }.ChatAddress.ToString());
        Assert.Equal("http://localhost:11434/", new LlmOptions { Provider = LlmProviderType.Ollama }.ChatAddress.ToString());
        Assert.Equal("http://box:1234/v1/", new LlmOptions { BaseAddress = "http://box:1234/v1" }.ChatAddress.ToString());
        Assert.Equal("http://embed:8081/v1/", new LlmOptions { EmbeddingBaseAddress = "http://embed:8081/v1" }.EmbeddingAddress.ToString());
    }

    [Fact]
    public async Task OpenAi_gets_the_key_and_max_completion_tokens_without_the_gpu_lease()
    {
        var options = new LlmOptions { Provider = LlmProviderType.OpenAi, ApiKey = "sk-test", Model = "gpt-5-mini" };
        var handler = new RecordingHandler(HttpStatusCode.OK, """{ "choices": [ { "message": { "content": "{}" } } ] }""");
        var lease = new CountingLease();

        await new OpenAiCompatibleClient(Http(handler, options), lease, Options.Create(options)).CompleteJsonAsync(Request());

        Assert.Equal("https://api.openai.com/v1/chat/completions", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer sk-test", handler.Request.Headers.Authorization!.ToString());
        Assert.Empty(lease.Acquired);

        var sent = JsonNode.Parse(handler.Sent!)!;
        Assert.Equal(700, sent["max_completion_tokens"]!.GetValue<int>());
        Assert.Null(sent["max_tokens"]);
    }

    [Fact]
    public async Task A_server_of_your_own_gets_no_key_unless_one_is_set()
    {
        var options = new LlmOptions { BaseAddress = "http://llm.local/v1/" };
        var handler = new RecordingHandler(HttpStatusCode.OK, """{ "choices": [ { "message": { "content": "{}" } } ] }""");

        await new OpenAiCompatibleClient(Http(handler, options), new CountingLease(), Options.Create(options)).CompleteJsonAsync(Request());

        Assert.Null(handler.Request!.Headers.Authorization);
    }

    [Fact]
    public async Task Ollama_puts_the_schema_in_format_turns_thinking_off_and_holds_the_lease()
    {
        var options = new LlmOptions { Provider = LlmProviderType.Ollama, Model = "gemma3:12b", Temperature = 0.5, ReasoningEffort = "none" };
        var handler = new RecordingHandler(HttpStatusCode.OK, """{ "message": { "role": "assistant", "content": "{\"text\":\"hi\"}" }, "done": true }""");
        var lease = new CountingLease();

        var answer = await new OllamaClient(Http(handler, options), lease, Options.Create(options)).CompleteJsonAsync(Request());

        Assert.Equal("""{"text":"hi"}""", answer);
        Assert.Equal("http://localhost:11434/api/chat", handler.Request!.RequestUri!.ToString());
        Assert.Equal([GpuConsumer.Llm], lease.Acquired);

        var sent = JsonNode.Parse(handler.Sent!)!;
        Assert.Equal("gemma3:12b", sent["model"]!.GetValue<string>());
        Assert.False(sent["stream"]!.GetValue<bool>());
        Assert.False(sent["think"]!.GetValue<bool>());
        Assert.Equal("object", sent["format"]!["type"]!.GetValue<string>());
        Assert.Equal(700, sent["options"]!["num_predict"]!.GetValue<int>());
        Assert.Equal("user", sent["messages"]![1]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Ollama_text_has_no_format_and_embeddings_read_the_first_vector()
    {
        var options = new LlmOptions { Provider = LlmProviderType.Ollama, Model = "m", EmbeddingModel = "nomic-embed-text" };
        var chat = new RecordingHandler(HttpStatusCode.OK, """{ "message": { "content": "prose" } }""");
        await new OllamaClient(Http(chat, options), new CountingLease(), Options.Create(options)).CompleteTextAsync(Request(null));

        Assert.Null(JsonNode.Parse(chat.Sent!)!["format"]);

        var embed = new RecordingHandler(HttpStatusCode.OK, """{ "embeddings": [ [0.5, -0.25, 1] ] }""");
        var lease = new CountingLease();
        var vector = await new OllamaEmbeddingClient(Http(embed, options), lease, Options.Create(options)).EmbedAsync("Rin laughed.");

        Assert.Equal([0.5f, -0.25f, 1f], vector);
        Assert.Equal("http://localhost:11434/api/embed", embed.Request!.RequestUri!.ToString());
        Assert.Equal([GpuConsumer.Embeddings], lease.Acquired);
    }

    [Fact]
    public async Task Google_sends_the_key_and_schema_and_leaves_out_thoughts()
    {
        var options = new LlmOptions { Provider = LlmProviderType.GoogleAi, ApiKey = "g-key", Model = "gemini-2.5-flash", ReasoningEffort = "none" };
        var handler = new RecordingHandler(HttpStatusCode.OK, """
            { "candidates": [ { "content": { "parts": [ { "text": "planning", "thought": true }, { "text": "{\"text\":" }, { "text": "\"hi\"}" } ] } } ] }
            """);

        var answer = await new GoogleAiClient(Http(handler, options), Options.Create(options)).CompleteJsonAsync(Request());

        Assert.Equal("""{"text":"hi"}""", answer);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent", handler.Request!.RequestUri!.ToString());
        Assert.Equal("g-key", Assert.Single(handler.Request.Headers.GetValues("x-goog-api-key")));

        var sent = JsonNode.Parse(handler.Sent!)!;
        var generation = sent["generationConfig"]!;
        Assert.Equal("application/json", generation["responseMimeType"]!.GetValue<string>());
        Assert.Equal("object", generation["responseJsonSchema"]!["type"]!.GetValue<string>());
        Assert.Equal(700, generation["maxOutputTokens"]!.GetValue<int>());
        Assert.Equal(0, generation["thinkingConfig"]!["thinkingBudget"]!.GetValue<int>());
        Assert.Equal("system", sent["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("user", sent["contents"]![0]!["parts"]![0]!["text"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("gemma-4-31b-it")]
    [InlineData("models/gemma-4-31b-it")]
    public async Task Google_sends_Gemma_no_thinking_budget_which_it_refuses(string model)
    {
        var options = new LlmOptions { Provider = LlmProviderType.GoogleAi, ApiKey = "k", Model = model, ReasoningEffort = "none" };
        var handler = new RecordingHandler(HttpStatusCode.OK, """{ "candidates": [ { "content": { "parts": [ { "text": "{\"text\":\"hi\"}" } ] } } ] }""");

        await new GoogleAiClient(Http(handler, options), Options.Create(options)).CompleteJsonAsync(Request());

        var generation = JsonNode.Parse(handler.Sent!)!["generationConfig"]!.AsObject();
        Assert.False(generation.ContainsKey("thinkingConfig"));
        Assert.Equal("object", generation["responseJsonSchema"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_blocked_prompt_from_Google_is_an_exception_with_its_reason()
    {
        var options = new LlmOptions { Provider = LlmProviderType.GoogleAi, ApiKey = "k", Model = "models/gemini-2.5-flash" };
        var handler = new RecordingHandler(HttpStatusCode.OK, """{ "promptFeedback": { "blockReason": "SAFETY" } }""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GoogleAiClient(Http(handler, options), Options.Create(options)).CompleteJsonAsync(Request()));

        Assert.Contains("SAFETY", ex.Message, StringComparison.Ordinal);
        Assert.EndsWith("/models/gemini-2.5-flash:generateContent", handler.Request!.RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Google_embeddings_read_the_values()
    {
        var options = new LlmOptions { Provider = LlmProviderType.GoogleAi, ApiKey = "k", EmbeddingModel = "gemini-embedding-001" };
        var handler = new RecordingHandler(HttpStatusCode.OK, """{ "embedding": { "values": [0.5, -0.25, 1] } }""");

        var vector = await new GoogleAiEmbeddingClient(Http(handler, options), Options.Create(options)).EmbedAsync("Rin laughed.");

        Assert.Equal([0.5f, -0.25f, 1f], vector);
        Assert.EndsWith("/models/gemini-embedding-001:embedContent", handler.Request!.RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, typeof(OpenAiCompatibleClient), typeof(OpenAiCompatibleEmbeddingClient))]
    [InlineData("OpenAi", typeof(OpenAiCompatibleClient), typeof(OpenAiCompatibleEmbeddingClient))]
    [InlineData("ollama", typeof(OllamaClient), typeof(OllamaEmbeddingClient))]
    [InlineData("GoogleAi", typeof(GoogleAiClient), typeof(GoogleAiEmbeddingClient))]
    public void The_configured_provider_decides_the_clients(string? provider, Type chat, Type embeddings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Llm:Provider"] = provider })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IGpuLease>(new CountingLease());
        services.AddLlm(config);
        using var built = services.BuildServiceProvider();

        Assert.IsType(chat, built.GetRequiredService<ILlmClient>());
        Assert.IsType(embeddings, built.GetRequiredService<IEmbeddingClient>());
    }

    [Fact]
    public void An_unknown_provider_is_refused_with_the_known_ones()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Llm:Provider"] = "Anthropic" })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddLlm(config));

        Assert.Contains("Ollama", ex.Message, StringComparison.Ordinal);
    }
}
