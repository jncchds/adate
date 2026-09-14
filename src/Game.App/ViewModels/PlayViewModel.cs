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
/// The stage always shows a picture, or a spinner saying what is being drawn; the words and choices
/// scroll beside or below it. Scenes are saved as they happen, so coming back shows the same one.
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

    // An opening's meeting place: the play screen goes straight into it instead of showing the map.
    private string? _startAt;

    // Where the player was last, so the map shows them there.
    private PlaceRecord? _stagePlace;

    // Bumped whenever the scene on screen changes, so a picture or answer for an earlier one is dropped.
    private int _scene;

    // Pictures, reused while the slot and weather they were drawn for last.
    private readonly Dictionary<string, Bitmap> _thumbnails = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Bitmap> _portraits = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Bitmap> _standing = new(StringComparer.Ordinal);
    private string? _picturesFor;
    private Bitmap? _backdrop;
    private int _generation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsError), nameof(IsScene), nameof(IsMap))]
    [NotifyPropertyChangedFor(nameof(IsEndingOffer), nameof(IsEnding), nameof(IsNoOpening), nameof(IsOver), nameof(IsStageLoading))]
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
    [NotifyPropertyChangedFor(nameof(IsStageLoading))]
    private Bitmap? _stageBackground;

    /// <summary>What the spinner on an empty stage says is on its way.</summary>
    [ObservableProperty]
    private string _stageLoadingText = "Opening the save…";

    [ObservableProperty]
    private Bitmap? _stageSprite;

    [ObservableProperty]
    private string? _sceneText;

    /// <summary>The name of whoever the scene is about, shown on a tag above the words.</summary>
    [ObservableProperty]
    private string? _speaker;

    /// <summary>Set while the model writes the scene or the reaction; a spinner stands in for the words.</summary>
    [ObservableProperty]
    private bool _isWriting;

    [ObservableProperty]
    private string _writingText = "Writing the scene…";

    [ObservableProperty]
    private string? _withLine;

    [ObservableProperty]
    private string? _revealLine;

    /// <summary>Something about the turn itself, such as a missed shift.</summary>
    [ObservableProperty]
    private string? _noteLine;

    /// <summary>People whose number the player has, to text from the map.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContacts))]
    private IReadOnlyList<ChoiceItem> _contacts = [];

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

    /// <param name="startAt">An opening's meeting place, to go straight into instead of showing the map.</param>
    public PlayViewModel(MainViewModel main, GameServices services, SaveId saveId, string? startAt = null)
    {
        _main = main;
        _world = services.Get<WorldService>();
        _studio = services.Get<CharacterStudio>();
        _placeTypes = services.Get<ILocationCatalog>();
        _files = services.Get<ImageFiles>();
        _saveId = saveId;
        _startAt = startAt;
    }

    /// <summary>Raised when new words replace the old ones, so the view scrolls back to their start.</summary>
    public event Action? ScrollToTopRequested;

    /// <summary>Raised when the conversation grows, so the view follows it to the newest words.</summary>
    public event Action? ScrollToEndRequested;

    /// <summary>The scene's conversation: each reply the player gave and the answer to it.</summary>
    public ObservableCollection<ExchangeItem> Exchanges { get; } = [];

    public bool IsLoading => Mode == PlayMode.Loading;

    public bool IsError => Mode == PlayMode.Error;

    public bool IsScene => Mode == PlayMode.Scene;

    public bool IsMap => Mode == PlayMode.Map;

    public bool IsEndingOffer => Mode == PlayMode.EndingOffer;

    public bool IsEnding => Mode == PlayMode.Ending;

    public bool IsNoOpening => Mode == PlayMode.NoOpening;

    public bool IsOver => Mode == PlayMode.Over;

    public bool IsStageLoading => StageBackground is null && Mode != PlayMode.Error;

    public bool HasNotes => Notes.Count > 0;

    public bool HasContacts => Contacts.Count > 0;

    public bool HasRecap => Recap.Count > 0;

    /// <summary>Who to bring along on the map, or who to stay with at the end.</summary>
    public ObservableCollection<PersonCardViewModel> People { get; } = [];

    public ObservableCollection<PlaceCardViewModel> Places { get; } = [];

    /// <summary>The map's stage: everyone met so far standing on the backdrop, then empty slots for the rest.</summary>
    public ObservableCollection<LineupItem> Lineup { get; } = [];

    public override async Task LoadAsync()
    {
        await ReloadAsync();

        // Choosing an opening leads straight into its meeting, not to a map with a hint.
        var start = _startAt;
        _startAt = null;
        if (start is not null && Mode == PlayMode.Map && Places.FirstOrDefault(p => p.Place.Id == start) is { } card)
        {
            await GoAsync(card);
        }
    }

    private async Task ReloadAsync()
    {
        Error = null;

        try
        {
            var state = await _world.GetPlayStateAsync(_saveId);
            _state = state;
            Apply(state);

            if (state.Scene is { } scene)
            {
                _ = RestoreSceneAsync(scene);
            }
            else
            {
                _ = LoadPicturesAsync(state);
            }
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    /// <summary>Shows the save as it stands, in the same order of precedence as the web page.</summary>
    private void Apply(PlayState state)
    {
        _scene++;
        _outcome = null;
        _view = null;
        Exchanges.Clear();
        IsWriting = false;
        ReplyText = "";
        WithLine = null;
        RevealLine = null;
        StageSprite = null;
        Speaker = null;
        People.Clear();
        HasPeople = false;
        Places.Clear();
        Lineup.Clear();
        Contacts = [];
        NoteLine = null;

        if (state.Scene is { } scene)
        {
            ShowStoredScene(state, scene);
        }
        else if (state.PendingScene is { } waiting)
        {
            Heading = $"Day {waiting.Clock.Day}, {waiting.Clock.Slot}";
            SceneText = waiting.Text;
            _view = new SceneView(waiting.Text, null, null, null, null, waiting.Choices);
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
            StageLoadingText = "Setting the scene…";
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
            StageLoadingText = "Setting the scene…";
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
                // What there is to do there: the player's shift first when it is now, then the place's own activities.
                var things = new List<ChoiceItem>();
                if (state.OnShift && state.Job?.Place == place.Id)
                {
                    things.Add(new ChoiceItem("Work your shift", DoCommand, new PlaceActivityChoice(place, PlayerLife.ShiftId)));
                }

                var type = _placeTypes.Get(place.TypeId);
                things.AddRange((type.Activities ?? []).Select(a => new ChoiceItem(a.Label, DoCommand, new PlaceActivityChoice(place, a.Id))));
                Places.Add(new PlaceCardViewModel(place, type.DisplayName, GoCommand, things));
            }

            Contacts = [.. (state.Contacts ?? []).Select(c => new ChoiceItem($"Text {c.Name}", TextCommand, new PersonTarget(c.Key, c.Name)))];

            // Everyone met so far stands on the backdrop, left to right, in one slot for each person the
            // story has; the row fills the stage once all of them are met.
            var met = state.Met ?? [];
            foreach (var person in met)
            {
                Lineup.Add(new LineupItem(person.Key, person.Name, true));
            }

            for (var empty = met.Count; empty < state.CastSize; empty++)
            {
                Lineup.Add(new LineupItem($"slot-{empty}", "", false));
            }

            StageLoadingText = "Setting the scene…";
            Mode = PlayMode.Map;
        }

        RefreshSceneControls();
        ScrollToTopRequested?.Invoke();
    }

    /// <summary>The scene the player left, exactly as it was: place, words, person, reply and reaction.</summary>
    private void ShowStoredScene(PlayState state, CurrentScene scene)
    {
        var outcome = scene.Outcome;
        var place = state.KnownPlaces.FirstOrDefault(p => p.Id == outcome.PlaceId);
        _stagePlace = place ?? _stagePlace;
        _outcome = outcome with { Text = scene.Text };
        _view = new SceneView(scene.Text, scene.CharacterId, scene.Speaker, null, scene.Expression, state.PendingScene?.Choices);

        Heading = $"Day {outcome.VisitedAt.Day}, {outcome.VisitedAt.Slot}";
        StageCaption = place?.Name ?? outcome.PlaceId;
        StageLoadingText = $"Drawing {StageCaption}…";
        SceneText = scene.Written ? scene.Text : null;
        IsWriting = !scene.Written;
        WritingText = "Writing the scene…";
        WithLine = outcome.With.Count > 0 ? $"With: {string.Join(", ", outcome.With.Select(Who))}" : null;
        RevealLine = outcome.Reveals.Count > 0 ? $"New place: {string.Join(", ", outcome.Reveals.Select(PlaceName))}" : null;
        NoteLine = outcome.Note;
        Speaker = scene.Speaker;
        foreach (var exchange in scene.Exchanges)
        {
            Exchanges.Add(new ExchangeItem(exchange.Reply) { Reaction = exchange.Reaction, Popup = exchange.Popup, Agreed = exchange.Agreed });
        }

        Mode = PlayMode.Scene;
    }

    /// <summary>
    /// Brings back a stored scene's pictures (drawn already, so they come from the cache), draws anything
    /// the scene was still waiting for, and finishes the writing if leaving interrupted it.
    /// </summary>
    private async Task RestoreSceneAsync(CurrentScene scene)
    {
        var token = _scene;

        try
        {
            var background = await PictureAsync(scene.BackgroundPath ?? await _world.SceneBackgroundAsync(_saveId));
            if (token != _scene)
            {
                return;
            }

            StageBackground = background;
        }
        catch (Exception)
        {
            if (token == _scene)
            {
                StageLoadingText = "The picture could not be drawn.";
            }
        }

        if (scene.CharacterId is not null)
        {
            try
            {
                var path = scene.SpritePath;
                if (path is null)
                {
                    var presented = await _world.PresentAsync(_saveId, scene.Outcome);
                    path = await _world.SceneSpriteAsync(_saveId, presented with { Expression = scene.Expression ?? presented.Expression });
                }

                var sprite = await PictureAsync(path);
                if (token == _scene)
                {
                    StageSprite = sprite;
                }
            }
            catch (Exception)
            {
                // The words and choices work without the person's picture.
            }
        }

        if (!scene.Written)
        {
            Working = true;
            try
            {
                if (await _world.ResumeSceneAsync(_saveId) is { } written && token == _scene)
                {
                    await ApplyWrittenAsync(written, token);
                }
            }
            catch (Exception ex)
            {
                if (token == _scene)
                {
                    ShowError(ex);
                }
            }
            finally
            {
                Working = false;
            }
        }
    }

    /// <summary>
    /// What a scene offers now: the encounter's own choices before anything is said, what the player can
    /// say next while the conversation goes on (with a reply of their own), or Continue once it has closed.
    /// Nothing while words are still being written.
    /// </summary>
    private void RefreshSceneControls()
    {
        IReadOnlyList<ProposedChoice>? replies = null;
        IReadOnlyList<EncounterChoice>? authored = null;
        var showContinue = false;

        if (IsWriting || Mode != PlayMode.Scene)
        {
            // The spinner stands in for the words and the choices alike; outside a scene there is nothing to pick.
        }
        else if (Exchanges.Count == 0 && (_outcome?.Choices ?? _state?.Pending?.Choices) is { Count: > 0 } choices)
        {
            authored = choices;
        }
        else if (_view?.Choices is { Count: > 0 } offered)
        {
            replies = offered;
        }
        else
        {
            showContinue = true;
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
    /// Fills in the map's pictures as they arrive, never holding up the screen: the stage first, then the
    /// people on offer at their resting expression, then each place at this slot and weather.
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
            if (Mode is PlayMode.EndingOffer or PlayMode.Ending or PlayMode.Map || People.Count > 0)
            {
                _backdrop ??= await PictureAsync(await _studio.GenerateBackdropAsync());
                if (Stale())
                {
                    return;
                }
            }

            // The map, the ending offer and the ending stand on the neutral backdrop.
            if (Mode is PlayMode.EndingOffer or PlayMode.Ending or PlayMode.Map)
            {
                StageBackground = _backdrop;
            }
            else if ((_stagePlace
                      ?? state.KnownPlaces.FirstOrDefault(p => p.Id == state.LastPlaceId)
                      ?? state.KnownPlaces.FirstOrDefault()) is { } here)
            {
                if (StageBackground is null)
                {
                    StageLoadingText = $"Drawing {here.Name}…";
                }

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
            StageLoadingText = "The picture could not be drawn.";
        }

        // The lineup: each person met, at their resting expression, drawn once and cached.
        foreach (var item in Lineup.Where(i => i.IsMet).ToList())
        {
            if (!_standing.TryGetValue(item.Key, out var standing))
            {
                try
                {
                    standing = await PictureAsync(await _world.PortraitAsync(_saveId, item.Key));
                    if (standing is not null)
                    {
                        _standing[item.Key] = standing;
                    }
                }
                catch (Exception)
                {
                    // An empty slot is shown instead; the map works without the picture.
                }
            }

            if (Stale())
            {
                return;
            }

            item.Portrait = standing;
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

    /// <summary>
    /// While the player reads a scene, draws what the next map needs: every known place at the slot and
    /// weather the clock has moved on to. They are cached, so the map shows them straight away.
    /// </summary>
    private async Task PrewarmMapAsync()
    {
        try
        {
            var next = await _world.GetPlayStateAsync(_saveId);
            if (next.Over || next.EndingOffer is not null || next.Ending is not null)
            {
                return;
            }

            var weather = next.Weather?.Id ?? "clear";
            foreach (var place in next.KnownPlaces)
            {
                await _studio.GenerateBackgroundAsync(_saveId, place, next.Clock.Slot, weather);
            }
        }
        catch (Exception)
        {
            // Only a head start: the map draws whatever is still missing when it opens.
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

    /// <summary>Passing time at a place.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task GoAsync(PlaceCardViewModel? card) =>
        card is null
            ? Task.CompletedTask
            : TurnAsync(card.Place.Name, card.Place, () => _world.TakeTurnAsync(_saveId, card.Place.Id, string.IsNullOrEmpty(_invite) ? null : _invite));

    /// <summary>Doing something at a place: one of its activities, or the player's shift.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task DoAsync(object? parameter) =>
        parameter is PlaceActivityChoice choice
            ? TurnAsync(choice.Place.Name, choice.Place, () => _world.TakeTurnAsync(
                _saveId, choice.Place.Id, string.IsNullOrEmpty(_invite) ? null : _invite, choice.ActivityId))
            : Task.CompletedTask;

    /// <summary>Spending the slot texting someone whose number the player has.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task TextAsync(object? parameter) =>
        parameter is PersonTarget target
            ? TurnAsync($"Messages · {target.Name}", null, () => _world.TextAsync(_saveId, target.Key))
            : Task.CompletedTask;

    /// <param name="caption">What the stage says: the place, or the conversation.</param>
    /// <param name="place">Where the turn is spent, remembered for the map; null for texting.</param>
    private async Task TurnAsync(string caption, PlaceRecord? place, Func<Task<TurnOutcome>> take)
    {
        Working = true;
        var token = ++_scene;

        try
        {
            // On screen at once: the place's name, a spinner while its picture is drawn, and one while the
            // words are written. The placeholder text never shows unless the model cannot write the scene.
            _outcome = null;
            _view = null;
            StageBackground = null;
            StageSprite = null;
            Speaker = null;
            Exchanges.Clear();
            WithLine = null;
            RevealLine = null;
            NoteLine = null;
            SceneText = null;
            StageCaption = caption;
            StageLoadingText = place is null ? "Opening your messages…" : $"Drawing {place.Name}…";
            WritingText = "Writing the scene…";
            IsWriting = true;
            Mode = PlayMode.Scene;
            RefreshSceneControls();
            ScrollToTopRequested?.Invoke();

            var outcome = await take();
            _invite = "";
            _outcome = outcome;
            if (place is not null)
            {
                _stagePlace = place;
            }

            Heading = $"Day {outcome.VisitedAt.Day}, {outcome.VisitedAt.Slot}";
            WithLine = outcome.With.Count > 0 ? $"With: {string.Join(", ", outcome.With.Select(Who))}" : null;
            RevealLine = outcome.Reveals.Count > 0 ? $"New place: {string.Join(", ", outcome.Reveals.Select(PlaceName))}" : null;
            NoteLine = outcome.Note;

            // Whoever the scene is about steps in at their resting expression.
            var presented = await _world.PresentAsync(_saveId, outcome);
            _view = presented;
            Speaker = presented.Name;

            // The picture, the person and the words at once: the image service and the model work side by side.
            var background = ShowBackgroundAsync(token);
            var sprite = ShowSpriteAsync(presented, token);
            var written = await _world.WriteSceneAsync(_saveId, outcome);

            if (token != _scene)
            {
                return;
            }

            // The resting sprite is saved before the written expression's, so the later one stays.
            if (written.CharacterId is not null && written.Expression != presented.Expression)
            {
                await sprite;
            }

            await ApplyWrittenAsync(written, token);
            await background;
            await sprite;

            _ = PrewarmMapAsync();
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

    private async Task ShowBackgroundAsync(int token)
    {
        try
        {
            var picture = await PictureAsync(await _world.SceneBackgroundAsync(_saveId));
            if (token == _scene)
            {
                StageBackground = picture;
            }
        }
        catch (Exception)
        {
            if (token == _scene)
            {
                StageLoadingText = "The picture could not be drawn.";
            }
        }
    }

    private async Task ShowSpriteAsync(SceneView view, int token)
    {
        if (view.CharacterId is null)
        {
            return;
        }

        try
        {
            var picture = await PictureAsync(await _world.SceneSpriteAsync(_saveId, view));

            // A written expression that arrived meanwhile has its own picture; this one is out of date.
            if (token == _scene && _view?.Expression == view.Expression)
            {
                StageSprite = picture;
            }
        }
        catch (Exception)
        {
            // The scene works without the person's picture.
        }
    }

    /// <summary>The written scene replaces the spinner; a different expression brings its own picture.</summary>
    private async Task ApplyWrittenAsync(SceneView written, int token)
    {
        var expressionChanged = written.CharacterId is not null && written.Expression != _view?.Expression;

        _outcome = _outcome is null ? null : _outcome with { Text = written.Text };
        _view = written;
        Speaker = written.Name ?? Speaker;
        SceneText = written.Text;
        IsWriting = false;
        RefreshSceneControls();

        if (expressionChanged)
        {
            try
            {
                var picture = await PictureAsync(await _world.SceneSpriteAsync(_saveId, written));
                if (token == _scene)
                {
                    StageSprite = picture;
                }
            }
            catch (Exception)
            {
                // Keeps the resting picture.
            }
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
        var token = _scene;
        var exchange = BeginExchange(choice.Text);

        try
        {
            // The people there answer the choice like any reply; only a choice nobody hears moves straight on.
            if (await _world.ChooseAsync(_saveId, choice.Id) is { } result)
            {
                if (token != _scene)
                {
                    return;
                }

                ApplyReaction(exchange, result);
                await ShowReactionSpriteAsync(result, token);
            }
            else
            {
                await ContinueCoreAsync();
            }
        }
        catch (Exception ex)
        {
            Exchanges.Remove(exchange);
            ShowError(ex);
        }
        finally
        {
            Working = false;
        }
    }

    /// <summary>The player's words join the conversation at once, with a spinner while the others answer.</summary>
    private ExchangeItem BeginExchange(string words)
    {
        var exchange = new ExchangeItem(words);
        Exchanges.Add(exchange);
        WritingText = Speaker is { } who ? $"{who} is answering…" : "Writing what happens…";
        IsWriting = true;
        RefreshSceneControls();
        ScrollToEndRequested?.Invoke();
        return exchange;
    }

    /// <summary>The answer joins the conversation; new replies keep it going, and without them Continue ends it.</summary>
    private void ApplyReaction(ExchangeItem exchange, ReactionResult result)
    {
        exchange.Reaction = result.View.Text;
        exchange.Popup = result.Popup;
        exchange.Agreed = result.Agreed;
        _view = (_view ?? result.View) with { Choices = result.Next is { Count: > 0 } next ? next : null };
        IsWriting = false;
        RefreshSceneControls();
        ScrollToEndRequested?.Invoke();
    }

    /// <summary>A different expression in the answer brings the person's picture for it.</summary>
    private async Task ShowReactionSpriteAsync(ReactionResult result, int token)
    {
        if (result.View.CharacterId is null || _view is null || result.View.Expression == _view.Expression)
        {
            return;
        }

        _view = _view with
        {
            CharacterId = result.View.CharacterId,
            Name = result.View.Name ?? _view.Name,
            Aesthetic = result.View.Aesthetic ?? _view.Aesthetic,
            Expression = result.View.Expression,
        };

        try
        {
            var picture = await PictureAsync(await _world.SceneSpriteAsync(_saveId, result.View));
            if (token == _scene)
            {
                StageSprite = picture;
            }
        }
        catch (Exception)
        {
            // Keeps the picture already shown.
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
        var words = index is { } i ? Choices.ElementAtOrDefault(i)?.Text : ReplyText.Trim();
        if (string.IsNullOrWhiteSpace(words))
        {
            return;
        }

        Working = true;
        var token = _scene;
        var freeText = index is null ? ReplyText : null;
        var exchange = BeginExchange(words);

        try
        {
            var result = await _world.RespondAsync(_saveId, index, freeText);
            if (token != _scene)
            {
                return;
            }

            ReplyText = "";
            ApplyReaction(exchange, result);
            await ShowReactionSpriteAsync(result, token);
        }
        catch (Exception ex)
        {
            Exchanges.Remove(exchange);
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

    private async Task ContinueCoreAsync()
    {
        await _world.CloseSceneAsync(_saveId);
        _outcome = null;
        _view = null;
        await ReloadAsync();
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
        IsWriting = false;
        Mode = PlayMode.Error;
    }

    private Task<Bitmap?> PictureAsync(string? relativePath, int decodeWidth = 0) =>
        Pictures.LoadAsync(_files.PathOf(relativePath), decodeWidth);

    private string PlaceName(string placeId) =>
        _state?.Setting.Places.FirstOrDefault(p => p.Id == placeId)?.Name ?? placeId;

    private string Who(string reference) =>
        _state?.People.GetValueOrDefault(reference) ?? reference;
}
