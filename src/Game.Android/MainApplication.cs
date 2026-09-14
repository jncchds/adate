using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using Game.App;

namespace Game.Android;

[Application]
public sealed class MainApplication : AvaloniaAndroidApplication<App.App>
{
    public MainApplication(nint javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        // Saves, pictures and the player's server addresses live in the app's private storage.
        var data = FilesDir?.AbsolutePath ?? throw new InvalidOperationException("Android gave the app no files directory.");
        App.App.Paths = new GamePaths(ContentAssets.Extract(Assets!, data), data);

        return base.CustomizeAppBuilder(builder).WithInterFont();
    }
}
