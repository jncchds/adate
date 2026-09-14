using Game.Core.Cast;
using Game.Core.Story;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Game.Llm;

public static class LlmServiceCollectionExtensions
{
    /// <summary>
    /// Registers the scene writer. The endpoint is configuration: the LLM is independently
    /// addressable and normally does not share a machine with the game. Needs the story and cast
    /// content, and a GPU lease, registered by the host.
    /// </summary>
    public static IServiceCollection AddLlm(this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        services.Configure<LlmOptions>(config.GetSection(LlmOptions.SectionName));

        services.AddHttpClient<ILlmClient, OpenAiCompatibleClient>(static (sp, http) =>
        {
            var options = sp.GetRequiredService<IOptions<LlmOptions>>().Value;
            http.BaseAddress = OpenAiCompatibleClient.EnsureTrailingSlash(options.BaseAddress);
            http.Timeout = options.RequestTimeout;
        });

        services.AddSingleton(static sp => new SceneValidator(sp.GetRequiredService<StoryContent>(), sp.GetRequiredService<CastContent>()));
        services.AddTransient<SceneWriter>();

        return services;
    }
}
