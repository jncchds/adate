namespace Game.Llm;

/// <summary>Which API the game writes with. A game has one, chosen in <see cref="LlmOptions.Provider"/>.</summary>
public enum LlmProviderType
{
    /// <summary><c>chat/completions</c> on a server of your own: LM Studio, llama.cpp, vLLM, or anything else speaking the shape.</summary>
    OpenAiCompatible,

    /// <summary>OpenAI's own API, with an API key.</summary>
    OpenAi,

    /// <summary>Ollama's native API (<c>api/chat</c>, <c>api/embed</c>).</summary>
    Ollama,

    /// <summary>Gemini through Google AI Studio (<c>generateContent</c>), with an API key.</summary>
    GoogleAi,
}

/// <summary>What the game knows about each provider: its name, where it lives by default, and what it needs.</summary>
public static class LlmProviders
{
    public static IReadOnlyList<LlmProviderType> All { get; } = Enum.GetValues<LlmProviderType>();

    public static string Label(LlmProviderType provider) => provider switch
    {
        LlmProviderType.OpenAiCompatible => "OpenAI-compatible (LM Studio, llama.cpp, vLLM)",
        LlmProviderType.OpenAi => "OpenAI",
        LlmProviderType.Ollama => "Ollama",
        LlmProviderType.GoogleAi => "Google AI Studio (Gemini)",
        _ => provider.ToString(),
    };

    /// <summary>The API root used when no address is configured.</summary>
    public static string DefaultAddress(LlmProviderType provider) => provider switch
    {
        LlmProviderType.OpenAi => "https://api.openai.com/v1/",
        LlmProviderType.Ollama => "http://localhost:11434/",
        LlmProviderType.GoogleAi => "https://generativelanguage.googleapis.com/v1beta/",
        _ => "http://localhost:1234/v1/",
    };

    /// <summary>Hosted providers refuse calls without a key; servers of your own usually take none.</summary>
    public static bool NeedsApiKey(LlmProviderType provider) => provider is LlmProviderType.OpenAi or LlmProviderType.GoogleAi;

    /// <summary>Only an OpenAI-compatible server with one model loaded can pick the model itself.</summary>
    public static bool NeedsModel(LlmProviderType provider) => provider is not LlmProviderType.OpenAiCompatible;

    /// <summary>
    /// Whether the model runs on the GPU the pictures share, so its calls hold the GPU lease (HANDOFF 1.6).
    /// A hosted model does not, and holding the lease for it would only stall the pictures.
    /// </summary>
    public static bool UsesLocalGpu(LlmProviderType provider) => provider is LlmProviderType.OpenAiCompatible or LlmProviderType.Ollama;

    public static Uri EnsureTrailingSlash(string address) =>
        new(address.EndsWith('/') ? address : address + "/");
}
