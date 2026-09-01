namespace Game.Core.Gpu;

/// <summary>
/// The Spike 0 lease. There is exactly one GPU consumer, so acquisition is free and
/// release does nothing. Exists so that call sites are already correct when
/// <c>SwapGpuLease</c> (12/16GB) or <c>CoResidentGpuLease</c> (24GB+) replaces it.
/// </summary>
public sealed class NoOpGpuLease : IGpuLease
{
    private static readonly IAsyncDisposable Handle = new NullHandle();

    public Task<IAsyncDisposable> AcquireAsync(GpuConsumer consumer, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Handle);
    }

    private sealed class NullHandle : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
