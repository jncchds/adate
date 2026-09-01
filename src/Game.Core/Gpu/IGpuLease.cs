namespace Game.Core.Gpu;

/// <summary>
/// HANDOFF 1.6: every GPU call is wrapped in a lease, from day one, even while the
/// implementation does nothing. On a 12GB card the real system decides whether the image
/// model and the LLM can be co-resident or must be swapped, and that logic has to slot in
/// without touching a single call site.
/// </summary>
/// <remarks>
/// The three model services are independently addressable and may sit on different
/// machines entirely, so a lease is not necessarily arbitrating one physical device.
/// Which implementation is used is configuration, not a compile-time choice.
/// </remarks>
public interface IGpuLease
{
    Task<IAsyncDisposable> AcquireAsync(GpuConsumer consumer, CancellationToken ct = default);
}
