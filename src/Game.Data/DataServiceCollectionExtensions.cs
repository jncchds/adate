using Game.Data.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Game.Data;

public static class DataServiceCollectionExtensions
{
    public static IServiceCollection AddData(this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        services.Configure<DatabaseOptions>(config.GetSection(DatabaseOptions.SectionName));

        services.AddSingleton<Database>();
        services.AddSingleton<SaveRepository>();
        services.AddSingleton<CharacterRepository>();
        services.AddSingleton<ImageCacheRepository>();
        services.AddSingleton<PlaceRepository>();
        services.AddSingleton<GameStateRepository>();
        services.AddSingleton<StoryStateRepository>();
        services.AddSingleton<MemoryRepository>();
        services.AddSingleton<SceneLogRepository>();
        services.AddSingleton<ThreadRepository>();
        services.AddSingleton<PlanRepository>();

        return services;
    }
}
