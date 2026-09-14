using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Game.Core.Settings;
using Game.Data.Repositories;

namespace Game.App.ViewModels;

/// <summary>Saves to continue, a new game, and where the servers are.</summary>
public sealed partial class HomeViewModel(MainViewModel main, GameServices services) : PageViewModel
{
    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private bool _hasSaves;

    public ObservableCollection<SaveItem> Saves { get; } = [];

    public string DataFolder => services.Paths.DataRoot;

    public override async Task LoadAsync()
    {
        try
        {
            var saves = services.Get<SaveRepository>();
            var settings = services.Get<ISettingCatalog>().All();

            Saves.Clear();
            foreach (var save in await saves.ListAsync())
            {
                var settingId = await saves.GetSettingIdAsync(save.Id);
                var setting = settingId is null ? null : settings.FirstOrDefault(s => s.Id == settingId)?.DisplayName;
                Saves.Add(new SaveItem(save.Id, setting ?? "No setting yet", $"started {save.CreatedUtc.ToLocalTime():g}", OpenCommand));
            }

            HasSaves = Saves.Count > 0;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private void Open(SaveItem? save)
    {
        if (save is not null)
        {
            main.ShowPlay(save.Id);
        }
    }

    [RelayCommand]
    private void NewGame() => main.ShowNewGame();

    [RelayCommand]
    private void Settings() => main.ShowSettings();
}
