using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Game.Llm;

namespace Game.App.ViewModels;

/// <summary>One entry in the provider list, shown by its label.</summary>
public sealed record ProviderChoice(LlmProviderType Type)
{
    public override string ToString() => LlmProviders.Label(Type);
}

/// <summary>Which language model provider the game uses, where it and the image service run. Saving restarts the game.</summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly MainViewModel _main;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddressHint))]
    private ProviderChoice _llmProvider;

    [ObservableProperty]
    private string _llmAddress;

    [ObservableProperty]
    private string _llmApiKey;

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
        ServerSettings current;
        try
        {
            current = ServerSettings.From(GameServices.BuildConfiguration(main.Paths));
        }
        catch (InvalidOperationException ex)
        {
            // An unknown provider in the file is exactly what this page is for fixing.
            current = new(LlmProviderType.OpenAiCompatible, "", "", "", "", "");
            _error = ex.Message;
        }

        _llmProvider = Providers.First(p => p.Type == current.LlmProvider);
        _llmAddress = current.LlmAddress;
        _llmApiKey = current.LlmApiKey;
        _llmModel = current.LlmModel;
        _embeddingModel = current.EmbeddingModel;
        _imageAddress = current.ImageAddress;
    }

    public IReadOnlyList<ProviderChoice> Providers { get; } = [.. LlmProviders.All.Select(p => new ProviderChoice(p))];

    public string AddressHint => LlmProviders.DefaultAddress(LlmProvider.Type);

    public string DataFolder => _main.Paths.DataRoot;

    // A hosted provider lives at one address, so an address typed for a server of your own would only be wrong there.
    partial void OnLlmProviderChanged(ProviderChoice value)
    {
        if (LlmProviders.NeedsApiKey(value.Type))
        {
            LlmAddress = "";
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        Error = null;

        try
        {
            var provider = LlmProvider.Type;
            var label = LlmProviders.Label(provider);

            if ((!string.IsNullOrWhiteSpace(LlmAddress) && !Uri.TryCreate(LlmAddress.Trim(), UriKind.Absolute, out _))
                || !Uri.TryCreate(ImageAddress.Trim(), UriKind.Absolute, out _))
            {
                throw new ArgumentException("Addresses must be full URLs, such as http://192.168.2.33:1234/v1/. Leave the model's empty for the provider's default.");
            }

            if (LlmProviders.NeedsApiKey(provider) && string.IsNullOrWhiteSpace(LlmApiKey))
            {
                throw new ArgumentException($"{label} needs an API key.");
            }

            if (LlmProviders.NeedsModel(provider) && string.IsNullOrWhiteSpace(LlmModel))
            {
                throw new ArgumentException($"{label} needs a scene model.");
            }

            new ServerSettings(provider, LlmAddress, LlmApiKey, LlmModel, EmbeddingModel, ImageAddress).Save(_main.Paths.SettingsFile);
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
