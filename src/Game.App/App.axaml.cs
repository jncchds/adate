using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Game.App.ViewModels;
using Game.App.Views;

namespace Game.App;

public partial class App : Application
{
    /// <summary>Set by each head before the app starts; desktop paths when a head leaves it unset.</summary>
    public static GamePaths? Paths { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // A lifetime-less start (the headless test platform) builds no main view and starts no game.
        MainViewModel? main = null;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            main = new MainViewModel(Paths ?? GamePaths.ForDesktop());
            desktop.MainWindow = new MainWindow { DataContext = main };
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime activity)
        {
            // The activity can be recreated (rotation, returning from the background); every view it
            // asks for shares the one game.
            main = new MainViewModel(Paths ?? GamePaths.ForDesktop());
            activity.MainViewFactory = () => new MainView { DataContext = main };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            main = new MainViewModel(Paths ?? GamePaths.ForDesktop());
            single.MainView = new MainView { DataContext = main };
        }

        _ = main?.StartAsync();

        base.OnFrameworkInitializationCompleted();
    }
}
