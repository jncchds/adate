using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Game.Core.Saves;
using Game.Play;

namespace Game.App.ViewModels;

/// <summary>How the player meets the love interest: the setting's openings.</summary>
public sealed partial class OpeningViewModel(MainViewModel main, GameServices services, SaveId saveId) : PageViewModel
{
    private readonly WorldService _world = services.Get<WorldService>();

    [ObservableProperty]
    private string _heading = "How you meet";

    [ObservableProperty]
    private string? _lede;

    [ObservableProperty]
    private IReadOnlyList<OpeningItem> _openings = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChooseCommand))]
    private bool _working;

    [ObservableProperty]
    private string? _error;

    public override async Task LoadAsync()
    {
        Error = null;

        try
        {
            var state = await _world.GetPlayStateAsync(saveId);
            if (state.Opening is not null)
            {
                main.ShowPlay(saveId);
                return;
            }

            string PlaceName(string placeId) => state.Setting.Places.FirstOrDefault(p => p.Id == placeId)?.Name ?? placeId;

            Heading = $"How you meet {state.MainLiName}";
            Lede = $"{state.Setting.DisplayName}. Choose how the story starts.";
            Openings =
            [
                .. state.Setting.Openings.Select(o =>
                    new OpeningItem(o, $"{PlaceName(o.MeetingPlace)}, {o.Time.ToString().ToLowerInvariant()}", ChooseCommand)),
            ];
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    private bool CanChoose() => !Working;

    [RelayCommand(CanExecute = nameof(CanChoose))]
    private async Task ChooseAsync(OpeningItem? item)
    {
        if (item is null)
        {
            return;
        }

        Working = true;

        try
        {
            await _world.ChooseOpeningAsync(saveId, item.Opening.Id);

            // Straight into the meeting the opening describes, rather than a map with a hint.
            main.ShowPlay(saveId, item.Opening.MeetingPlace);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            Working = false;
        }
    }

    [RelayCommand]
    private Task RetryAsync() => LoadAsync();

    [RelayCommand]
    private void Home() => main.ShowHome();
}
