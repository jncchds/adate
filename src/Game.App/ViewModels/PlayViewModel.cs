using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Game.Core.Content;
using Game.Core.Encounters;
using Game.Core.Places;
using Game.Core.Saves;
using Game.Core.Story;
using Game.Play;

namespace Game.App.ViewModels;

public enum PlayMode
{
    Loading,
    Error,
    Scene,
    Map,
    EndingOffer,
    Ending,
    NoOpening,
    Over,
}

/// <summary>
/// A save in play: the map, scenes with their replies and reactions, the ending offer and the ending.
/// The stage always shows a picture; the words and choices scroll beside or below it.
/// </summary>
public sealed partial class PlayViewModel : PageViewModel
{
    private readonly MainViewModel _main;
    private readonly WorldService _world;
    private readonly CharacterStudio _studio;
    private readonly ILocationCatalog _placeTypes;
    private readonly ImageFiles _files;
    private readonly SaveId _saveId;

    private PlayState? _state;
    private TurnOutcome? _outcome;
    private SceneView? _view;
    private string _invite = "";

    // Where the player last was, so the map shows them there. Not saved: after a restart the map
    // opens on the first known place.
    private PlaceRecord? _stagePlace;

    // Pictures, reused while the slot and weather they were drawn for last.
    private readonly Dictionary<string, Bitmap> _thumbnails = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Bitmap> _portraits = new(StringComparer.Ordinal);
    private string? _picturesFor;
    private Bitmap? _backdrop;
    private int _generation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsError), nameof(IsScene), nameof(IsMap))]
    [NotifyPropertyChangedFor(nameof(IsEndingOffer), nameof(IsEnding), nameof(IsNoOpening), nameof(IsOver))]
    private PlayMode _mode = PlayMode.Loading;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoCommand), nameof(PickPersonCommand), nameof(EndCommand), nameof(ChooseCommand))]
    [NotifyCanExecuteChangedFor(nameof(RespondCommand), nameof(ReplyCommand), nameof(ContinueCommand), nameof(RetryCommand))]
    private bool _working;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private string _heading = "Play";

    [ObservableProperty]
    private string? _lede;

    [ObservableProperty]
    private string? _stageCaption;

    [ObservableProperty]
    private Bitmap? _stageBackground;

    [ObservableProperty]
    private Bitmap? _stageSprite;

    [ObservableProperty]
    private string? _sceneText;

    /// <summary>The name of whoever the scene is about, shown on a tag above the words.</summary>
    [ObservableProperty]
    private string? _speaker;

    [ObservableProperty]
    private string? _withLine;

    [ObservableProperty]
    private string? _revealLine;

    [ObservableProperty]
    private IReadOnlyList<ChoiceItem> _choices = [];

    [ObservableProperty]
    private bool _canReply;

    [ObservableProperty]
    private bool _showContinue;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReplyCommand))]
    private string _replyText = "";

    [ObservableProperty]
    private string? _reaction;

    [ObservableProperty]
    private string? _popup;

    [ObservableProperty]
    private string? _agreed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotes))]
    private IReadOnlyList<string> _notes = [];

    [ObservableProperty]
    private bool _hasPeople;

    [ObservableProperty]
    private string? _passedOver;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecap))]
    private IReadOnlyList<RecapItem> _recap = [];

    [ObservableProperty]
    private string? _profileOf;

    [ObservableProperty]
    private IReadOnlyList<string> _profile = [];

    public PlayViewModel(MainViewModel main, GameServices services, SaveId saveId)
    {
        _main = main;
        _world = services.Get<WorldService>();
        _studio = services.Get<CharacterStudio>();
        _placeTypes = services.Get<ILocationCatalog>();
        _files = services.Get<ImageFiles>();
        _saveId = saveId;
    }

    /// <summary>Raised when new words replace the old ones, so the view scrolls back to their start.</summary>
    public event Action? ScrollToTopRequested;

    public bool IsLoading => Mode == PlayMode.Loading;

    public bool IsError => Mode == PlayMode.Error;

    public bool IsScene => Mode == PlayMode.Scene;

    public bool IsMap => Mode == PlayMode.Map;

    public bool IsEndingOffer => Mode == PlayMode.EndingOffer;

    public bool IsEnding => Mode == PlayMode.Ending;

    public bool IsNoOpening => Mode == PlayMode.NoOpening;

    public bool IsOver => Mode == PlayMode.Over;

    public bool HasNotes => Notes.Count > 0;

    public bool HasRecap => Recap.Count > 0;

    /// <summary>Who to bring along on the map, or who to stay with at the end.</summary>
    public ObservableCollection<PersonCardViewModel> People { get; } = [];

    public ObservableCollection<PlaceCardViewModel> Places { get; } = [];

    public override Task LoadAsync() => ReloadAsync();

    private async Task ReloadAsync()
    {
        Error = null;

        try
        {
            var state = await _world.GetPlayStateAsync(_saveId);
            _state = state;
            Apply(state);
            _ = LoadPicturesAsync(state);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    /// <summary>Shows the save as it stands between turns, in the same order of precedence as the web page.</summary>
    private void Apply(PlayState state)
    {
        _outcome = null;
        _view = null;
        Reaction = null;
        Popup = null;
        Agreed = null;
        ReplyText = "";
        WithLine = null;
        RevealLine = null;
        StageSprite = null;
        Speaker = null;
        People.Clear();
        HasPeople = false;
        Places.Clear();

        if (state.PendingScene is { } waiting)
        {
            Heading = $"Day {waiting.Clock.Day}, {waiting.Clock.Slot}";
            SceneText = waiting.Text;
            Mode = PlayMode.Scene;
        }
        else if (state.Ending is { } ending)
        {
            Heading = ending.PartnerName is null ? "On your own" : $"With {ending.PartnerName}";
            SceneText = ending.Text;
            Notes = ending.Departures;
            PassedOver = ending.PassedOver.Count > 0 ? string.Join(", ", ending.PassedOver) : null;
            Recap =
            [
                .. (ending.Choices ?? []).Select(line => new RecapItem(
                    $"Day {line.Day}, {line.Slot.ToLowerInvariant()}",
                    $"“{line.Words}”",
                    string.Join(" · ", line.Influence))),
            ];
            ProfileOf = ending.ProfileOf;
            Profile = ending.Profile;
            StageCaption = null;
            Mode = PlayMode.Ending;
        }
        else if (state.EndingOffer is { } offer)
        {
            Heading = state.Over ? $"The {state.Setting.Days} days are over" : "It is time to decide";
            Notes = offer.Departures;
            Lede = offer.Routes.Count == 0
                ? "No one is waiting for an answer."
                : offer.Asker is { } asker ? $"{asker} asks whether you'll stay. Who do you stay with?" : "Who do you stay with?";

            foreach (var route in offer.Routes)
            {
                People.Add(new PersonCardViewModel(route.Key, $"Stay with {route.Name}", false, EndCommand));
            }

            People.Add(new PersonCardViewModel(EndingRules.AloneKey, "Leave on your own", true, EndCommand));
            HasPeople = true;
            StageCaption = null;
            Mode = PlayMode.EndingOffer;
        }
        else if (state.Pending is { } pending)
        {
            Heading = $"Day {state.Clock.Day}";
            SceneText = pending.Text;
            Mode = PlayMode.Scene;
        }
        else if (state.Opening is null)
        {
            Heading = state.Setting.DisplayName;
            Lede = $"This save has not chosen how you meet {state.MainLiName} yet.";
            Mode = PlayMode.NoOpening;
        }
        else if (state.Over)
        {
            Heading = $"The {state.Setting.Days} days are over";
            Lede = "The calendar stops here.";
            Mode = PlayMode.Over;
        }
        else
        {
            var slot = state.Clock.Slot.ToString();
            Heading = $"Day {state.Clock.Day} of {state.Setting.Days}";
            StageCaption = state.Weather is { } weather ? $"{slot} · {weather.Label}" : slot;
            Lede = $"{state.Setting.DisplayName}. Choose where to spend the {slot.ToLowerInvariant()}.";
            Notes =
            [
                .. state.Today.Select(ev => $"Today: {ev.Name} at {PlaceName(ev.Place)}, {ev.Time.ToString().ToLowerInvariant()}."),
                .. state.Hints,
            ];

            _invite = "";
            if (state.Invitees.Count > 0)
            {
                People.Add(new PersonCardViewModel("", "Nobody", true, PickPersonCommand) { IsChosen = true });
                foreach (var person in state.Invitees)
                {
                    People.Add(new PersonCardViewModel(person.Key, person.Name, false, PickPersonCommand));
                }

                HasPeople = true;
            }

            foreach (var place in state.KnownPlaces)
            {
                Places.Add(new PlaceCardViewModel(place, _placeTypes.Get(place.TypeId).DisplayName, GoCommand));
            }

            Mode = PlayMode.Map;
        }

        RefreshSceneControls();
        ScrollToTopRequested?.Invoke();
    }

    /// <summary>
    /// What a scene offers now: the encounter's own choices, the proposed replies with a free reply,
    /// or Continue once a reaction has been written.
    /// </summary>
    private void RefreshSceneControls()
    {
        IReadOnlyList<ProposedChoice>? replies = null;
        IReadOnlyList<EncounterChoice>? authored = null;
        var showContinue = false;

        if (_outcome is not null)
        {
            if (_outcome.Choices is { Count: > 0 } choices)
            {
                authored = choices;
            }
            else if (Reaction is not null)
            {
                showContinue = true;
            }
            else if (_view?.Choices is { Count: > 0 } offered)
            {
                replies = offered;
            }
            else
            {
                showContinue = true;
            }
        }
        else if (_state?.PendingScene is { } waiting)
        {
            if (Reaction is not null)
            {
                showContinue = true;
            }
            else
            {
                replies = waiting.Choices;
            }
        }
        else if (_state?.Pending is { } pending && Mode == PlayMode.Scene)
        {
            authored = pending.Choices;
        }

        Choices = authored is not null
            ? [.. authored.Select(c => new ChoiceItem(c.Text, ChooseCommand, c))]
            : replies is not null
                ? [.. replies.Select((c, i) => new ChoiceItem(c.Text, RespondCommand, i))]
                : [];
        CanReply = replies is not null;
        ShowContinue = showContinue;
    }

    /// <summary>
    /// Fills in pictures as they arrive, never holding up the screen: the stage first, then the people
    /// on offer at their resting expression, then each place at this slot and weather.
    /// </summary>
    private async Task LoadPicturesAsync(PlayState state)
    {
        var generation = ++_generation;
        var weather = state.Weather?.Id ?? "clear";
        var key = $"{state.Clock.Day}:{state.Clock.Slot}:{weather}";
        if (_picturesFor != key)
        {
            _thumbnails.Clear();
            _picturesFor = key;
        }

        bool Stale() => generation != _generation || _outcome is not null;

        try
        {
            if (Mode is PlayMode.EndingOffer or PlayMode.Ending || People.Count > 0)
            {
                _backdrop ??= await PictureAsync(await _studio.GenerateBackdropAsync());
                if (Stale())
                {
                    return;
                }
            }

            if (Mode is PlayMode.EndingOffer or PlayMode.Ending)
            {
                StageBackground = _backdrop;
            }
            else if ((_stagePlace ?? state.KnownPlaces.FirstOrDefault()) is { } here)
            {
                var background = await PictureAsync(await _studio.GenerateBackgroundAsync(_saveId, here, state.Clock.Slot, weather));
                if (Stale())
                {
                    return;
                }

                StageBackground = background;
            }
        }
        catch (Exception)
        {
            // The stage keeps whatever it showed; the choices below still work.
        }

        foreach (var card in People.ToList())
        {
            if (card.IsNobody)
            {
                continue;
            }

            card.Backdrop = _backdrop;

            if (!_portraits.TryGetValue(card.Key, out var portrait))
            {
                try
                {
                    portrait = await PictureAsync(await _world.PortraitAsync(_saveId, card.Key), 400);
                    if (portrait is not null)
                    {
                        _portraits[card.Key] = portrait;
                    }
                }
                catch (Exception)
                {
                    // A missing picture leaves the card with its name; the choice still works.
                }
            }

            if (Stale())
            {
                return;
            }

            card.Portrait = portrait;
        }

        foreach (var card in Places.ToList())
        {
            if (!_thumbnails.TryGetValue(card.Place.Id, out var thumbnail))
            {
                try
                {
                    thumbnail = await PictureAsync(await _studio.GenerateBackgroundAsync(_saveId, card.Place, state.Clock.Slot, weather), 480);
                    if (thumbnail is not null && _picturesFor == key)
                    {
                        _thumbnails[card.Place.Id] = thumbnail;
                    }
                }
                catch (Exception)
                {
                    // As above: the place stays choosable by name.
                }
            }

            // The player moved on while this one was drawing; stop drawing for a screen that is gone.
            if (Stale())
            {
                return;
            }

            card.Thumbnail = thumbnail;
        }
    }

    private bool CanAct() => !Working;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private void PickPerson(PersonCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        foreach (var person in People)
        {
            person.IsChosen = person == card;
        }

        _invite = card.Key;
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task GoAsync(PlaceCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        Working = true;

        try
        {
            var place = card.Place;
            var outcome = await _world.TakeTurnAsync(_saveId, place.Id, string.IsNullOrEmpty(_invite) ? null : _invite);
            _invite = "";
            _outcome = outcome;
            _view = null;
            _stagePlace = place;

            Heading = $"Day {outcome.VisitedAt.Day}, {outcome.VisitedAt.Slot}";
            StageCaption = place.Name;
            SceneText = outcome.Text;
            WithLine = outcome.With.Count > 0 ? $"With: {string.Join(", ", outcome.With.Select(Who))}" : null;
            RevealLine = outcome.Reveals.Count > 0 ? $"New place: {string.Join(", ", outcome.Reveals.Select(PlaceName))}" : null;
            Reaction = null;
            Popup = null;
            Agreed = null;
            StageSprite = null;
            Speaker = null;
            Mode = PlayMode.Scene;
            RefreshSceneControls();
            ScrollToTopRequested?.Invoke();

            // Drawn at the slot the visit happened in, not the one the clock moved on to.
            StageBackground = await PictureAsync(
                await _studio.GenerateBackgroundAsync(_saveId, place, outcome.VisitedAt.Slot, _state?.Weather?.Id ?? "clear"));

            // Whoever the scene is about steps in at their resting expression straight away.
            _view = await _world.PresentAsync(_saveId, outcome);
            Speaker = _view.Name;
            StageSprite = await PictureAsync(await _world.SpriteAsync(_saveId, _view));

            // Written once the placeholder, background and sprite are on screen, so a slow or absent
            // model never holds up the turn itself. The written expression replaces the resting one.
            var written = await _world.WriteSceneAsync(_saveId, outcome);
            _outcome = outcome with { Text = written.Text };
            SceneText = written.Text;

            var expressionChanged = written.Expression != _view.Expression;
            _view = written;
            RefreshSceneControls();

            if (expressionChanged)
            {
                StageSprite = await PictureAsync(await _world.SpriteAsync(_saveId, written));
            }
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            Working = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task ChooseAsync(EncounterChoice? choice)
    {
        if (choice is null)
        {
            return;
        }

        Working = true;

        try
        {
            await _world.ChooseAsync(_saveId, choice.Id);
            await ContinueCoreAsync();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            Working = false;
        }
    }

    /// <summary>A proposed reply, by its index in the offered replies.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task RespondAsync(object? parameter) => parameter is int index ? AnswerAsync(index) : Task.CompletedTask;

    private bool CanReplyNow() => !Working && !string.IsNullOrWhiteSpace(ReplyText);

    /// <summary>The player's own words.</summary>
    [RelayCommand(CanExecute = nameof(CanReplyNow))]
    private Task ReplyAsync() => AnswerAsync(null);

    private async Task AnswerAsync(int? index)
    {
        if (index is null && string.IsNullOrWhiteSpace(ReplyText))
        {
            return;
        }

        Working = true;

        try
        {
            var result = await _world.RespondAsync(_saveId, index, index is null ? ReplyText : null);
            Reaction = result.View.Text;
            Popup = result.Popup;
            Agreed = result.Agreed;
            ReplyText = "";
            RefreshSceneControls();

            if (_outcome is not null && result.View.CharacterId is not null && result.View.Expression != _view?.Expression)
            {
                _view = result.View;
                StageSprite = await PictureAsync(await _world.SpriteAsync(_saveId, result.View));
            }
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            Working = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task EndAsync(PersonCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        foreach (var person in People)
        {
            person.IsChosen = person == card;
        }

        Working = true;
        Lede = "Writing the ending.";

        try
        {
            await _world.EndAsync(_saveId, card.Key);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            Working = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task ContinueAsync() => ContinueCoreAsync();

    private Task ContinueCoreAsync()
    {
        _outcome = null;
        _view = null;
        return ReloadAsync();
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task RetryAsync()
    {
        _outcome = null;
        return ReloadAsync();
    }

    [RelayCommand]
    private void Home() => _main.ShowHome();

    [RelayCommand]
    private void Opening() => _main.ShowOpening(_saveId);

    private void ShowError(Exception ex)
    {
        Error = ex.Message;
        Heading = "Something went wrong";
        Speaker = null;
        Mode = PlayMode.Error;
    }

    private Task<Bitmap?> PictureAsync(string? relativePath, int decodeWidth = 0) =>
        Pictures.LoadAsync(_files.PathOf(relativePath), decodeWidth);

    private string PlaceName(string placeId) =>
        _state?.Setting.Places.FirstOrDefault(p => p.Id == placeId)?.Name ?? placeId;

    private string Who(string reference) =>
        _state?.People.GetValueOrDefault(reference) ?? reference;
}
