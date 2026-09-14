using CommunityToolkit.Mvvm.ComponentModel;
using Game.Core.Saves;

namespace Game.App.ViewModels;

/// <summary>The running game and the screen in front: every page navigates through here.</summary>
public sealed partial class MainViewModel(GamePaths paths) : ObservableObject
{
    private GameServices? _services;
    private Task? _start;

    [ObservableProperty]
    private PageViewModel _page = new StartingViewModel(null);

    public GamePaths Paths { get; } = paths;

    /// <summary>Starts the game once, off the UI thread; later calls return the same start.</summary>
    public Task StartAsync() => _start ??= StartCoreAsync();

    /// <summary>Starts again with the current settings: after the player changes server addresses.</summary>
    public async Task RestartAsync()
    {
        Page = new StartingViewModel(null);

        if (_services is not null)
        {
            await _services.DisposeAsync();
            _services = null;
        }

        _start = null;
        await StartAsync();
    }

    public void ShowHome() => Show(new HomeViewModel(this, Services));

    public void ShowNewGame() => Show(new NewGameViewModel(this, Services));

    public void ShowCandidates(Guid characterId) => Show(new CandidatesViewModel(this, Services, characterId));

    public void ShowOpening(SaveId saveId) => Show(new OpeningViewModel(this, Services, saveId));

    /// <param name="startAt">An opening's meeting place, to go straight into instead of showing the map.</param>
    public void ShowPlay(SaveId saveId, string? startAt = null) => Show(new PlayViewModel(this, Services, saveId, startAt));

    public void ShowSettings() => Show(new SettingsViewModel(this));

    private GameServices Services => _services ?? throw new InvalidOperationException("The game has not started.");

    private async Task StartCoreAsync()
    {
        try
        {
            _services = await Task.Run(() => GameServices.Start(Paths));
            ShowHome();
        }
        catch (Exception ex)
        {
            Page = new StartingViewModel(this) { Error = ex.Message };
        }
    }

    private void Show(PageViewModel page)
    {
        Page = page;
        _ = page.LoadAsync();
    }
}
