using Game.Core.Cast;
using Game.Core.Story;
using Game.Llm.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Game.Llm;

public static class LlmServiceCollectionExtensions
{
    public const string ProviderKey = "Llm:Provider";

    /// <summary>
    /// Registers the scene writer. The provider and its endpoint are configuration: the LLM is independently
    /// addressable and normally does not share a machine with the game. Needs the story and cast
    /// content, and a GPU lease, registered by the host.
    /// </summary>
    public static IServiceCollection AddLlm(this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        services.Configure<LlmOptions>(config.GetSection(LlmOptions.SectionName));

        // One provider per game, read once: changing it restarts the game, as changing an address does.
        switch (ProviderFrom(config))
        {
            case LlmProviderType.Ollama:
                AddClients<OllamaClient, OllamaEmbeddingClient>(services);
                break;
            case LlmProviderType.GoogleAi:
                AddClients<GoogleAiClient, GoogleAiEmbeddingClient>(services);
                break;
            default:
                AddClients<OpenAiCompatibleClient, OpenAiCompatibleEmbeddingClient>(services);
                break;
        }

        services.AddTransient<MemoryCompactor>();

        services.AddSingleton(static sp => new SceneValidator(sp.GetRequiredService<StoryContent>(), sp.GetRequiredService<CastContent>()));
        services.AddTransient<SceneJudge>();
        services.AddTransient<BibleWriter>();
        services.AddTransient<VoiceWriter>();
        services.AddTransient<ThreadWriter>();
        services.AddTransient<PlanWriter>();
        services.AddTransient<SceneWriter>();
        services.AddTransient<ReactionWriter>();
        services.AddTransient<EpilogueWriter>();

        return services;
    }

    public static LlmProviderType ProviderFrom(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var value = config[ProviderKey];
        if (string.IsNullOrWhiteSpace(value))
        {
            return LlmProviderType.OpenAiCompatible;
        }

        return Enum.TryParse<LlmProviderType>(value, ignoreCase: true, out var provider) && Enum.IsDefined(provider)
            ? provider
            : throw new InvalidOperationException(
                $"Unknown LLM provider '{value}' in '{ProviderKey}'. Known: {string.Join(", ", LlmProviders.All)}.");
    }

    private static void AddClients<TChat, TEmbedding>(IServiceCollection services)
        where TChat : class, ILlmClient
        where TEmbedding : class, IEmbeddingClient
    {
        services.AddHttpClient<ILlmClient, TChat>(static (sp, http) =>
        {
            var options = sp.GetRequiredService<IOptions<LlmOptions>>().Value;
            http.BaseAddress = options.ChatAddress;
            http.Timeout = options.RequestTimeout;
        });

        services.AddHttpClient<IEmbeddingClient, TEmbedding>(static (sp, http) =>
        {
            var options = sp.GetRequiredService<IOptions<LlmOptions>>().Value;
            http.BaseAddress = options.EmbeddingAddress;
            http.Timeout = options.RequestTimeout;
        });
    }
}
