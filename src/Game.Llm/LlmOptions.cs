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

    public int MaxTextLength { get; set; } = 1800;
}
