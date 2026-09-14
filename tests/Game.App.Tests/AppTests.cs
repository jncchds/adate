using Avalonia.Headless.XUnit;
using Game.App.ViewModels;
using Game.App.Views;

namespace Game.App.Tests;

public sealed class AppTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "adate-app-tests", Guid.NewGuid().ToString("N"));

    private GamePaths Paths => new(RepositoryRoot(), _data);

    [Fact]
    public async Task The_game_starts_from_shipped_content_with_no_servers_reachable()
    {
        await using var services = GameServices.Start(Paths);

        var home = new HomeViewModel(new MainViewModel(Paths), services);
        await home.LoadAsync();

        Assert.Null(home.Error);
        Assert.False(home.HasSaves);
        Assert.True(File.Exists(Paths.DatabaseFile));
    }

    [Fact]
    public async Task The_new_game_form_offers_every_setting_and_the_pack_s_subjects()
    {
        await using var services = GameServices.Start(Paths);

        var form = new NewGameViewModel(new MainViewModel(Paths), services);
        await form.LoadAsync();

        Assert.Null(form.Error);
        Assert.NotEmpty(form.Settings);
        Assert.NotEmpty(form.Subjects);
        Assert.All(form.Features, f => Assert.False(string.IsNullOrEmpty(f.Value), $"{f.Label} has a value."));
        Assert.All(form.Temper, t => Assert.NotNull(t.Selected));
    }

    [Fact]
    public void Server_addresses_the_player_saves_override_the_shipped_ones()
    {
        new ServerSettings("http://10.0.0.5:1234/v1/", "some-model", "some-embedding", "http://10.0.0.5:8000").Save(Paths.SettingsFile);

        var settings = ServerSettings.From(GameServices.BuildConfiguration(Paths));

        Assert.Equal("http://10.0.0.5:1234/v1/", settings.LlmAddress);
        Assert.Equal("some-model", settings.LlmModel);
        Assert.Equal("http://10.0.0.5:8000", settings.ImageAddress);
    }

    [AvaloniaFact]
    public void Every_view_loads_its_markup()
    {
        Assert.NotNull(new MainWindow());
        Assert.NotNull(new StartingView());
        Assert.NotNull(new HomeView());
        Assert.NotNull(new NewGameView());
        Assert.NotNull(new CandidatesView());
        Assert.NotNull(new OpeningView());
        Assert.NotNull(new PlayView());
        Assert.NotNull(new SettingsView());
    }

    [AvaloniaFact]
    public void The_window_shows_the_starting_page_before_the_game_is_up()
    {
        var window = new MainWindow { DataContext = new MainViewModel(Paths) };
        window.Show();

        var locator = new ViewLocator();
        Assert.IsType<StartingView>(locator.Build(((MainViewModel)window.DataContext).Page));
        window.Close();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_data, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a temp folder left behind is harmless.
        }
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.EnumerateFiles("*.slnx").Any() is false)
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
    }
}
