namespace Game.Llm;

/// <summary>Turns a memory summary into a vector for retrieval (plan §8).</summary>
public interface IEmbeddingClient
{
    /// <summary>The embedding. Throws when the endpoint cannot be reached or answers with an error.</summary>
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
}
