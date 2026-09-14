using Android.App;
using Android.Content.PM;
using Android.Views;
using Avalonia.Android;

namespace Game.Android;

[Activity(
    Label = "adate",
    Theme = "@style/AdateTheme",
    Icon = "@drawable/icon",
    MainLauncher = true,
    WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout |
                           ConfigChanges.SmallestScreenSize | ConfigChanges.UiMode)]
public sealed class MainActivity : AvaloniaMainActivity
{
}
