using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Game.App.ViewModels;
using Game.App.Views;

namespace Game.App;

/// <summary>Picks the view for a page. Listed by hand rather than by reflection, so trimming keeps them.</summary>
public sealed class ViewLocator : IDataTemplate
{
    public Control? Build(object? param) => param switch
    {
        StartingViewModel => new StartingView(),
        HomeViewModel => new HomeView(),
        NewGameViewModel => new NewGameView(),
        CandidatesViewModel => new CandidatesView(),
        OpeningViewModel => new OpeningView(),
        PlayViewModel => new PlayView(),
        SettingsViewModel => new SettingsView(),
        _ => new TextBlock { Text = $"No view for {param?.GetType().Name}" },
    };

    public bool Match(object? data) => data is PageViewModel;
}
