using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Game.App.ViewModels;

/// <summary>Shown while the game starts, and in its place when it could not.</summary>
/// <param name="main">Null while starting; set when there is a failure to retry or settings to fix.</param>
public sealed partial class StartingViewModel(MainViewModel? main) : PageViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Failed))]
    private string? _error;

    public bool Failed => Error is not null;

    [RelayCommand]
    private Task RetryAsync() => main?.RestartAsync() ?? Task.CompletedTask;

    [RelayCommand]
    private void Settings() => main?.ShowSettings();
}
