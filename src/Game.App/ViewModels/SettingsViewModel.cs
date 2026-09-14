using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Game.App.ViewModels;

/// <summary>Where the language model and the image service run. Saving restarts the game.</summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly MainViewModel _main;

    [ObservableProperty]
    private string _llmAddress;

    [ObservableProperty]
    private string _llmModel;

    [ObservableProperty]
    private string _embeddingModel;

    [ObservableProperty]
    private string _imageAddress;

    [ObservableProperty]
    private string? _error;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;

        // Read straight from the files rather than the running game, which may have failed to start.
        var current = ServerSettings.From(GameServices.BuildConfiguration(main.Paths));
        _llmAddress = current.LlmAddress;
        _llmModel = current.LlmModel;
        _embeddingModel = current.EmbeddingModel;
        _imageAddress = current.ImageAddress;
    }

    public string DataFolder => _main.Paths.DataRoot;

    [RelayCommand]
    private async Task SaveAsync()
    {
        Error = null;

        try
        {
            if (!Uri.TryCreate(LlmAddress.Trim(), UriKind.Absolute, out _) || !Uri.TryCreate(ImageAddress.Trim(), UriKind.Absolute, out _))
            {
                throw new ArgumentException("Both addresses must be full URLs, such as http://192.168.2.33:1234/v1/.");
            }

            new ServerSettings(LlmAddress, LlmModel, EmbeddingModel, ImageAddress).Save(_main.Paths.SettingsFile);
            await _main.RestartAsync();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private Task CancelAsync() => _main.RestartAsync();
}
