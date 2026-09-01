namespace Game.Core.Gpu;

/// <summary>Which subsystem wants the GPU. Used by swapping leases to decide what to evict.</summary>
public enum GpuConsumer
{
    Image = 0,
    Llm = 1,
    Embeddings = 2,
}
