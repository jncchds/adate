using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(Game.App.Tests.TestAppBuilder))]

namespace Game.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
