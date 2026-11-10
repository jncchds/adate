using System.Net;
using System.Text.Json.Nodes;
using Game.Llm;
using Game.Llm.Providers;
using Microsoft.Extensions.Options;

namespace Game.Llm.Tests;

public class GoogleAiQuotaTests
{
    private const string Daily =
        """{"error":{"code":429,"message":"You exceeded your current quota.","status":"RESOURCE_EXHAUSTED","details":[{"@type":"type.googleapis.com/google.rpc.QuotaFailure","violations":[{"quotaId":"GenerateRequestsPerDayPerProjectPerModel-FreeTier"}]},{"@type":"type.googleapis.com/google.rpc.RetryInfo","retryDelay":"17s"}]}}""";

    private const string PerMinute =
        """{"error":{"code":429,"message":"Rate limited.","details":[{"@type":"type.googleapis.com/google.rpc.RetryInfo","retryDelay":"0.1s"}]}}""";

    private const string Ok = """{"candidates":[{"content":{"parts":[{"text":"{\"a\":1}"}]}}]}""";

    private sealed class Script(params (HttpStatusCode, string)[] replies) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = replies[Math.Min(Calls++, replies.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private static GoogleAiClient Client(Script script) => new(
        new HttpClient(script) { BaseAddress = new Uri("http://google.test/") },
        Options.Create(new LlmOptions { Enabled = true, Model = "gemini-test", ApiKey = "k" }));

    private static readonly LlmRequest Request = new("s", "u", "scene", new JsonObject());

    [Fact]
    public async Task A_daily_limit_is_not_retried_and_says_so()
    {
        var script = new Script((HttpStatusCode.TooManyRequests, Daily));

        var quota = await Assert.ThrowsAsync<LlmQuotaException>(() => Client(script).CompleteJsonAsync(Request));

        Assert.Equal(1, script.Calls);
        Assert.True(quota.Daily);
        Assert.Contains("daily limit for gemini-test", quota.Message);
    }

    [Fact]
    public async Task A_long_wait_is_not_waited_out()
    {
        var script = new Script((HttpStatusCode.TooManyRequests, PerMinute.Replace("0.1s", "40s")));

        var quota = await Assert.ThrowsAsync<LlmQuotaException>(() => Client(script).CompleteJsonAsync(Request));

        Assert.Equal(1, script.Calls);
        Assert.False(quota.Daily);
    }

    [Fact]
    public async Task A_short_wait_is_retried_once()
    {
        var script = new Script((HttpStatusCode.TooManyRequests, PerMinute), (HttpStatusCode.OK, Ok));

        Assert.Equal("""{"a":1}""", await Client(script).CompleteJsonAsync(Request));
        Assert.Equal(2, script.Calls);
    }

    [Fact]
    public async Task A_spent_quota_is_not_asked_again_until_the_probe_time()
    {
        var now = DateTimeOffset.UtcNow;
        var usage = new LlmUsage { Now = () => now };
        var script = new Script((HttpStatusCode.TooManyRequests, Daily), (HttpStatusCode.OK, Ok));
        var counted = new CountingLlmClient(Client(script), usage);

        await Assert.ThrowsAsync<LlmQuotaException>(() => counted.CompleteJsonAsync(Request));
        Assert.NotNull(usage.Problem);

        await Assert.ThrowsAsync<LlmQuotaException>(() => counted.CompleteJsonAsync(Request));
        Assert.Equal(1, script.Calls);

        now += LlmUsage.DailyProbe + TimeSpan.FromSeconds(1);
        Assert.Equal("""{"a":1}""", await counted.CompleteJsonAsync(Request));
        Assert.Null(usage.Problem);
        Assert.Equal("scene 2", usage.Since(new Dictionary<string, int>()));
    }
}
