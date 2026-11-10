using System.Collections.Concurrent;

namespace Game.Llm;

/// <summary>
/// The model refused because a quota is spent, not because it is busy. Retrying costs more of the same
/// quota, so nothing retries it, and the player is told why the words are a placeholder.
/// </summary>
public sealed class LlmQuotaException(string message, TimeSpan? retryAfter, bool daily)
    : HttpRequestException(message, null, System.Net.HttpStatusCode.TooManyRequests)
{
    /// <summary>How long the provider says to wait, when it says.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;

    /// <summary>A per-day limit: nothing to do but wait for the reset, or use another model.</summary>
    public bool Daily { get; } = daily;
}

/// <summary>
/// What the player should know about the model right now (a spent quota), and a count of every call
/// made, by kind, so a turn's cost can be read and kept down. One per game.
/// </summary>
public sealed class LlmUsage
{
    /// <summary>How long a spent daily quota blocks calls before one is tried again to see whether it has reset.</summary>
    public static readonly TimeSpan DailyProbe = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, int> _calls = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private string? _problem;
    private DateTimeOffset _blockedUntil;

    public Func<DateTimeOffset> Now { get; init; } = static () => DateTimeOffset.UtcNow;

    /// <summary>The reason calls are failing, in words for the player; null when the last call worked.</summary>
    public string? Problem
    {
        get { lock (_gate) { return _problem; } }
    }

    public int Total => _calls.Values.Sum();

    /// <summary>Calls made so far, by kind (<c>scene</c>, <c>reaction</c>, <c>embed</c>…).</summary>
    public IReadOnlyDictionary<string, int> Calls => new Dictionary<string, int>(_calls);

    public void Count(string kind) => _calls.AddOrUpdate(kind, 1, static (_, n) => n + 1);

    /// <summary>The calls made since <paramref name="before"/> was taken from <see cref="Calls"/>, as <c>scene 1, judge 1</c>.</summary>
    public string Since(IReadOnlyDictionary<string, int> before)
    {
        ArgumentNullException.ThrowIfNull(before);

        var parts = _calls
            .Select(c => (c.Key, N: c.Value - before.GetValueOrDefault(c.Key)))
            .Where(c => c.N > 0)
            .OrderBy(c => c.Key, StringComparer.Ordinal)
            .Select(c => $"{c.Key} {c.N}");
        return string.Join(", ", parts);
    }

    public void Succeeded()
    {
        lock (_gate)
        {
            _problem = null;
            _blockedUntil = default;
        }
    }

    public void SpentQuota(LlmQuotaException quota)
    {
        ArgumentNullException.ThrowIfNull(quota);

        lock (_gate)
        {
            _problem = quota.Message;
            var wait = quota.Daily ? DailyProbe : quota.RetryAfter ?? TimeSpan.FromSeconds(30);
            _blockedUntil = Now() + wait;
        }
    }

    /// <summary>The quota error to throw instead of calling out, while a spent quota is still being waited out; null otherwise.</summary>
    public LlmQuotaException? Blocked()
    {
        lock (_gate)
        {
            return _problem is not null && Now() < _blockedUntil
                ? new LlmQuotaException(_problem, _blockedUntil - Now(), daily: true)
                : null;
        }
    }
}

/// <summary>Counts each call and keeps a spent quota from being asked again until it may have reset.</summary>
public sealed class CountingLlmClient(ILlmClient inner, LlmUsage usage) : ILlmClient
{
    public Task<string> CompleteJsonAsync(LlmRequest request, CancellationToken ct = default) => RunAsync(request, inner.CompleteJsonAsync, ct);

    public Task<string> CompleteTextAsync(LlmRequest request, CancellationToken ct = default) => RunAsync(request, inner.CompleteTextAsync, ct);

    private async Task<string> RunAsync(LlmRequest request, Func<LlmRequest, CancellationToken, Task<string>> call, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (usage.Blocked() is { } blocked)
        {
            throw blocked;
        }

        usage.Count(request.SchemaName);
        try
        {
            var answer = await call(request, ct).ConfigureAwait(false);
            usage.Succeeded();
            return answer;
        }
        catch (LlmQuotaException quota)
        {
            usage.SpentQuota(quota);
            throw;
        }
    }
}

public sealed class CountingEmbeddingClient(IEmbeddingClient inner, LlmUsage usage) : IEmbeddingClient
{
    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        usage.Count("embed");
        return inner.EmbedAsync(text, ct);
    }
}
