namespace Game.Llm;

/// <summary>
/// The chat model the game writes scenes with: any OpenAI-compatible endpoint (LM Studio, llama.cpp,
/// Ollama, vLLM). Off by default, so a machine without a model plays with placeholder text.
/// </summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    public bool Enabled { get; set; }

    /// <summary>The API root, ending in <c>/v1/</c>.</summary>
    public string BaseAddress { get; set; } = "http://localhost:1234/v1/";

    /// <summary>The model id the endpoint serves; empty lets servers with one model pick it.</summary>
    public string Model { get; set; } = "";

    /// <summary>Bounds one completion, including a model loading on first use.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>Retries after a rejected or failed answer, before the authored fallback is used (plan §8: two).</summary>
    public int MaxRetries { get; set; } = 2;

    public double Temperature { get; set; } = 0.8;

    /// <summary>
    /// Sent as <c>reasoning_effort</c> when set, for models that think before answering. <c>none</c>
    /// roughly halves a scene's latency on Gemma 4 with the schema still met; empty leaves the
    /// server's default.
    /// </summary>
    public string? ReasoningEffort { get; set; }

    public int MaxTextLength { get; set; } = 1500;

    /// <summary>The embedding model id for memory retrieval; empty turns retrieval off and memories fall back to recency.</summary>
    public string EmbeddingModel { get; set; } = "";

    /// <summary>The embeddings API root, when it is not <see cref="BaseAddress"/>.</summary>
    public string? EmbeddingBaseAddress { get; set; }

    /// <summary>How many retrieved memories go into a packet beside the last shared scene and the week.</summary>
    public int RetrievedMemories { get; set; } = 3;

    /// <summary>Whether a second, short call reads each scene's prose against the facts that cannot change.</summary>
    public bool UseJudge { get; set; } = true;
}
