using Avalonia;
using Game.App;

namespace Game.Desktop;

internal static class Program
{
    // Nothing that needs Avalonia or a synchronisation context may run before the app is started.
    [STAThread]
    public static void Main(string[] args)
    {
        App.App.Paths = GamePaths.ForDesktop();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the XAML previewer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App.App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
