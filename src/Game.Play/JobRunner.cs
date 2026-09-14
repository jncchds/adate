using System.Collections.Concurrent;

namespace Game.Play;

/// <summary>
/// Runs long generations outside any one Blazor circuit and deduplicates them by key.
/// </summary>
/// <remarks>
/// HANDOFF 9: a long generation must not block the circuit. Awaiting inside a component
/// already keeps the circuit responsive, but it does not survive the user navigating away
/// and back — the work would be abandoned and then started again from scratch, at the cost
/// of another 90 seconds of GPU time.
///
/// Holding the <see cref="Task"/> here instead means a component that re-renders, or a user
/// who returns to the page, rejoins the generation already in flight.
/// </remarks>
public sealed class JobRunner(ILogger<JobRunner> log)
{
    private readonly ConcurrentDictionary<string, Task> _jobs = new(StringComparer.Ordinal);

    /// <summary>
    /// Starts <paramref name="work"/> under <paramref name="key"/>, or returns the task
    /// already running under it.
    /// </summary>
    public Task<T> RunAsync<T>(string key, Func<CancellationToken, Task<T>> work)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(work);

        var task = _jobs.GetOrAdd(key, k =>
        {
            log.LogInformation("Starting generation job {Key}.", k);

            // Deliberately not tied to any request or circuit token: the whole point is to
            // outlive the component that asked for it.
            var started = work(CancellationToken.None);

            // A failed job must not be cached, or every retry would replay the same
            // exception without ever touching the GPU again.
            _ = started.ContinueWith(
                t =>
                {
                    if (t.IsFaulted)
                    {
                        log.LogError(t.Exception, "Generation job {Key} failed; it will be retried on request.", k);
                        _jobs.TryRemove(k, out _);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return started;
        });

        return (Task<T>)task;
    }

    public bool IsRunning(string key) =>
        _jobs.TryGetValue(key, out var task) && !task.IsCompleted;
}
