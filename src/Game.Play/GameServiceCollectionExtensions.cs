using Game.Core;
using Game.Core.Content;
using Game.Core.Style;
using Game.Data;
using Game.Imaging;
using Game.Imaging.Caching;
using Game.Llm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Game.Play;

public static class GameServiceCollectionExtensions
{
    /// <summary>
    /// Registers the whole game: data, imaging, the LLM, shipped content and the services that
    /// sequence them. Every frontend composes the same graph.
    /// </summary>
    /// <param name="contentRoot">
    /// Where style packs, content and workflow graphs are read from. They ship alongside the binary,
    /// so a host passes its base directory; Android passes the folder its assets were copied to.
    /// Writable state -- the database and the image store -- is not resolved against it, because
    /// that is state the player owns rather than content we ship.
    /// </param>
    public static IServiceCollection AddGame(this IServiceCollection services, IConfiguration config, string contentRoot)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);

        services.AddImaging(config);
        services.AddData(config);
        services.AddLlm(config);

        services.Configure<StudioOptions>(config.GetSection(StudioOptions.SectionName));

        // Content paths resolve against the content root rather than the working directory.
        // Otherwise `dotnet run` (cwd = project directory) and a published build (cwd = wherever it
        // was launched from) look in two different places for the same file.
        services.PostConfigure<StudioOptions>(o =>
        {
            o.StylePackDirectory = Resolve(o.StylePackDirectory);
            o.PlaceTypesFile = Resolve(o.PlaceTypesFile);
            o.SettingsDirectory = Resolve(o.SettingsDirectory);
            o.EncountersDirectory = Resolve(o.EncountersDirectory);
            o.PoseDirectory = Resolve(o.PoseDirectory);
            o.TemperFile = Resolve(o.TemperFile);
            o.WantsFile = Resolve(o.WantsFile);
            o.ContrastsFile = Resolve(o.ContrastsFile);
            o.ValuesFile = Resolve(o.ValuesFile);
            o.PredicatesFile = Resolve(o.PredicatesFile);
            o.RelationshipFile = Resolve(o.RelationshipFile);
            o.RoutesFile = Resolve(o.RoutesFile);
            o.EndingsFile = Resolve(o.EndingsFile);
            o.WeatherFile = Resolve(o.WeatherFile);
            o.HappeningsFile = Resolve(o.HappeningsFile);

            // Fail at startup rather than at the first render: a game that has declared an
            // impossible age floor should not show a single screen.
            o.Content.Validate();
        });

        services.PostConfigure<Game.Imaging.Workflows.WorkflowOptions>(o => o.Directory = Resolve(o.Directory));

        // Content: style packs and locations are data, not code (HANDOFF 1.5).
        services.AddSingleton(sp =>
            new JsonStylePackLoader(sp.GetRequiredService<IOptions<StudioOptions>>().Value.StylePackDirectory));
        services.AddSingleton<IStylePackLoader>(sp => sp.GetRequiredService<JsonStylePackLoader>());

        services.AddSingleton<ILocationCatalog>(sp =>
            new JsonLocationCatalog(sp.GetRequiredService<IOptions<StudioOptions>>().Value.PlaceTypesFile));

        services.AddSingleton<Game.Core.Settings.ISettingCatalog>(sp => new Game.Core.Settings.JsonSettingCatalog(
            sp.GetRequiredService<IOptions<StudioOptions>>().Value.SettingsDirectory,
            sp.GetRequiredService<ILocationCatalog>()));

        services.AddSingleton<Game.Core.Encounters.IEncounterCatalog>(sp => new Game.Core.Encounters.JsonEncounterCatalog(
            sp.GetRequiredService<IOptions<StudioOptions>>().Value.EncountersDirectory,
            sp.GetRequiredService<Game.Core.Settings.ISettingCatalog>(),
            [.. sp.GetRequiredService<Game.Core.Cast.RouteContent>().Routes.Select(r => r.Id)]));

        services.AddSingleton<WorldService>();

        services.AddSingleton(sp =>
        {
            var o = sp.GetRequiredService<IOptions<StudioOptions>>().Value;
            return Game.Core.Cast.CastContent.Load(o.TemperFile, o.WantsFile, o.ContrastsFile);
        });

        services.AddSingleton(sp =>
        {
            var o = sp.GetRequiredService<IOptions<StudioOptions>>().Value;
            var story = Game.Core.Story.StoryContent.Load(o.ValuesFile, o.PredicatesFile, o.RelationshipFile);
            story.ValidateAgainst(sp.GetRequiredService<Game.Core.Cast.CastContent>());
            return story;
        });

        services.AddSingleton(sp =>
        {
            var routes = Game.Core.Cast.RouteContent.Load(sp.GetRequiredService<IOptions<StudioOptions>>().Value.RoutesFile);
            routes.ValidateAgainst(sp.GetRequiredService<Game.Core.Cast.CastContent>());
            return routes;
        });

        services.AddSingleton(sp =>
            Game.Core.Story.EndingContent.Load(sp.GetRequiredService<IOptions<StudioOptions>>().Value.EndingsFile));

        services.AddSingleton(sp =>
        {
            var weather = Game.Core.World.WeatherContent.Load(sp.GetRequiredService<IOptions<StudioOptions>>().Value.WeatherFile);
            weather.ValidateAgainst(sp.GetRequiredService<ILocationCatalog>(), sp.GetRequiredService<Game.Core.Settings.ISettingCatalog>());
            return weather;
        });

        services.AddSingleton(sp =>
        {
            var happenings = Game.Core.Story.HappeningContent.Load(sp.GetRequiredService<IOptions<StudioOptions>>().Value.HappeningsFile);
            happenings.ValidateAgainst(
                sp.GetRequiredService<ILocationCatalog>().All().Select(t => t.Id),
                sp.GetRequiredService<Game.Core.World.WeatherContent>().Kinds.Select(k => k.Id));
            return happenings;
        });

        // One compiler per prompt dialect; the pack's dialect picks which one runs.
        services.AddSingleton<IPromptCompiler, BooruPromptCompiler>();
        services.AddSingleton<IPromptCompiler, NaturalPromptCompiler>();
        services.AddSingleton<PromptCompilers>();

        // Pose skeletons are shipped content, seeded into the image store on first use.
        services.AddSingleton(sp => new PoseCatalog(
            sp.GetRequiredService<IImageStore>(),
            sp.GetRequiredService<IOptions<StudioOptions>>().Value.PoseDirectory));

        services.AddSingleton<JobRunner>();
        services.AddSingleton<CharacterStudio>();
        services.AddSingleton<ImageFiles>();

        return services;

        string Resolve(string path) => Path.IsPathRooted(path) ? path : Path.Combine(contentRoot, path);
    }
}
