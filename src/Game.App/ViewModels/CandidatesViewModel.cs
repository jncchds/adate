using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Game.Core.Characters;
using Game.Data.Repositories;
using Game.Play;

namespace Game.App.ViewModels;

/// <summary>Candidate portraits for the new character; the chosen one becomes their look.</summary>
public sealed partial class CandidatesViewModel(MainViewModel main, GameServices services, Guid characterId) : PageViewModel
{
    private readonly CharacterStudio _studio = services.Get<CharacterStudio>();
    private readonly CharacterRepository _characters = services.Get<CharacterRepository>();
    private CharacterRecord? _character;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApproveCommand))]
    private CandidateItem? _selected;

    [ObservableProperty]
    private bool _loading;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApproveCommand))]
    [NotifyPropertyChangedFor(nameof(ApproveLabel))]
    private bool _approving;

    [ObservableProperty]
    private string? _error;

    public ObservableCollection<CandidateItem> Candidates { get; } = [];

    public string Lede =>
        $"One seed, {_studio.StylePackId}. The first portrait is exactly what you described; each of the others " +
        "moves one or two features to a close neighbour. Whichever you pick becomes this character's appearance.";

    public string LoadingText =>
        $"Generating {_studio.CandidateCount} portraits on the GPU box. It takes under a minute when the model " +
        "is already loaded, longer on a cold start.";

    public string ApproveLabel => Approving ? "Saving…" : "Use this look";

    public override async Task LoadAsync()
    {
        Error = null;
        Loading = true;
        Candidates.Clear();

        try
        {
            _character = await _characters.GetAsync(characterId)
                ?? throw new InvalidOperationException($"No character with id {characterId}.");

            // JobRunner deduplicates by key, so coming back here rejoins the generation in flight.
            var files = services.Get<ImageFiles>();
            foreach (var candidate in await _studio.GenerateCandidatesAsync(_character))
            {
                var item = new CandidateItem(candidate, Describe(candidate), SelectCommand);
                Candidates.Add(item);
                item.Picture = await Pictures.LoadAsync(files.PathOf(candidate.RelativePath), 512);
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            Loading = false;
        }
    }

    private static string Describe(Candidate candidate) =>
        candidate.Changes.Count == 0
            ? "as described"
            : string.Join("; ", candidate.Changes.Select(c => $"{c.From} → {c.To}"));

    [RelayCommand]
    private void Select(CandidateItem? item)
    {
        foreach (var candidate in Candidates)
        {
            candidate.IsSelected = candidate == item;
        }

        Selected = item;
    }

    private bool CanApprove() => Selected is not null && !Approving;

    [RelayCommand(CanExecute = nameof(CanApprove))]
    private async Task ApproveAsync()
    {
        if (Selected is null || _character is null)
        {
            return;
        }

        Approving = true;

        try
        {
            await _studio.ApproveAnchorAsync(_character.Id, Selected.Candidate);

            // The cast is built now, as records with no art, from the approved look (plan §3).
            var approved = await _characters.GetAsync(_character.Id)
                ?? throw new InvalidOperationException($"No character with id {_character.Id}.");
            await _studio.EnsureCastAsync(approved);

            main.ShowOpening(_character.SaveId);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            Approving = false;
        }
    }

    [RelayCommand]
    private Task RetryAsync() => LoadAsync();

    [RelayCommand]
    private void Home() => main.ShowHome();
}
