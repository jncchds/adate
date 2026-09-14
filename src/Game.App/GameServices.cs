using Game.Data;
using Game.Play;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Game.App;

/// <summary>
/// The game's services for one run of the app. Changing server addresses disposes it and starts a
/// new one, because the HTTP clients read their addresses once.
/// </summary>
public sealed class GameServices : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    private GameServices(ServiceProvider provider, GamePaths paths, IConfiguration configuration)
    {
        _provider = provider;
        Paths = paths;
        Configuration = configuration;
    }

    public GamePaths Paths { get; }

    public IConfiguration Configuration { get; }

    public T Get<T>()
        where T : notnull => _provider.GetRequiredService<T>();

    /// <summary>
    /// Builds the graph, upgrades the database and loads every content file, so a broken install
    /// fails here with its reason instead of on the first screen that needs the file.
    /// </summary>
    public static GameServices Start(GamePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        Directory.CreateDirectory(paths.DataRoot);
        var configuration = BuildConfiguration(paths);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGame(configuration, paths.ContentRoot);

        var provider = services.BuildServiceProvider();
        try
        {
            provider.GetRequiredService<Database>().Migrate();
            provider.GetRequiredService<WorldService>();
            return new GameServices(provider, paths, configuration);
        }
        catch
        {
            provider.Dispose();
            throw;
        }
    }

    /// <summary>Shipped defaults, then the player's addresses, then the head's writable paths.</summary>
    public static IConfiguration BuildConfiguration(GamePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        using var defaults = typeof(GameServices).Assembly.GetManifestResourceStream("Game.App.appsettings.json")
            ?? throw new InvalidOperationException("The shipped appsettings.json is missing from Game.App.");

        var builder = new ConfigurationBuilder().AddJsonStream(defaults);

        if (File.Exists(paths.SettingsFile))
        {
            builder.AddJsonFile(paths.SettingsFile, optional: true, reloadOnChange: false);
        }

        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Path"] = paths.DatabaseFile,
            ["ImageStore:Directory"] = paths.ImageDirectory,
        });

        return builder.Build();
    }

    public ValueTask DisposeAsync() => _provider.DisposeAsync();
}
