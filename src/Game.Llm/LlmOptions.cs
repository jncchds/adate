namespace Game.Llm;

/// <summary>
/// The chat model the game writes scenes with, through one <see cref="LlmProviderType">provider</see> per game.
/// Off by default, so a machine without a model plays with placeholder text.
/// </summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    public bool Enabled { get; set; }

    public LlmProviderType Provider { get; set; } = LlmProviderType.OpenAiCompatible;

    /// <summary>The API root, such as <c>http://host:1234/v1/</c>; empty uses the provider's <see cref="LlmProviders.DefaultAddress">default</see>.</summary>
    public string BaseAddress { get; set; } = "";

    /// <summary>Sent to providers that take a key; hosted ones need it.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>The model id the endpoint serves; empty lets OpenAI-compatible servers with one model pick it.</summary>
    public string Model { get; set; } = "";

    public Uri ChatAddress => LlmProviders.EnsureTrailingSlash(
        string.IsNullOrWhiteSpace(BaseAddress) ? LlmProviders.DefaultAddress(Provider) : BaseAddress.Trim());

    public Uri EmbeddingAddress => string.IsNullOrWhiteSpace(EmbeddingBaseAddress)
        ? ChatAddress
        : LlmProviders.EnsureTrailingSlash(EmbeddingBaseAddress.Trim());

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

    /// <summary>
    /// Writes a scene or reaction's prose in one plain-text call and reads its data (facts, choices, tags) out in a
    /// second, so the prose is not written under the schema.
    /// </summary>
    public bool TwoPass { get; set; }

    // The four kinds of material are on by default: in the replay evaluation (docs/decisions.md, "Measuring the
    // writing") they raised the judged scene score on both Gemma 12B (5.4 to 6.6) and 31B (6.6 to 7.1). Two-pass
    // writing stays off: it helped 12B less than the material did, cost language, and lowered 31B.

    /// <summary>Gives each love interest a written way of talking, generated once per person, and puts it in every packet.</summary>
    public bool Voices { get; set; } = true;

    /// <summary>Adds one small happening from content to ordinary scenes, so there is something going on to write about.</summary>
    public bool Happenings { get; set; } = true;

    /// <summary>Keeps the loose ends scenes leave open, and hands the open ones back to later scenes.</summary>
    public bool Threads { get; set; } = true;

    /// <summary>Asks for proposed replies that differ in kind and pick up what the player knows.</summary>
    public bool VariedChoices { get; set; } = true;
}
