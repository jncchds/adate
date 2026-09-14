using System.Windows.Input;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Game.Core.Places;
using Game.Core.Saves;
using Game.Core.Settings;
using Game.Play;

namespace Game.App.ViewModels;

/// <summary>A button in a list: its words, and what pressing it does.</summary>
public sealed record ChoiceItem(string Text, ICommand Command, object? Parameter);

public sealed record SaveItem(SaveId Id, string Title, string Started, ICommand Command);

public sealed record SettingItem(string Id, string Name, string Detail);

public sealed record RecapItem(string When, string Words, string Influence);

public sealed record TemperEndOption(string Id, string Label);

public sealed record OpeningItem(SettingOpening Opening, string Where, ICommand Command);

/// <summary>Doing something at a place: one of its activities, or the player's shift.</summary>
public sealed record PlaceActivityChoice(PlaceRecord Place, string ActivityId);

/// <summary>A debug section's lines as one block of text.</summary>
public static class DebugLines
{
    public static Avalonia.Data.Converters.IValueConverter Join { get; } =
        new Avalonia.Data.Converters.FuncValueConverter<IReadOnlyList<string>?, string>(lines => lines is null or { Count: 0 } ? "(none)" : string.Join('\n', lines));
}

/// <summary>Someone to act towards, such as to text.</summary>
public sealed record PersonTarget(string Key, string Name);

/// <summary>One temper axis in the new-game form, with the writing of whichever end is picked.</summary>
public sealed partial class TemperAxisItem : ObservableObject
{
    private readonly Func<string, string> _writing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Writing))]
    private TemperEndOption? _selected;

    public TemperAxisItem(string id, string label, IReadOnlyList<TemperEndOption> ends, Func<string, string> writing)
    {
        Id = id;
        Label = label;
        Ends = ends;
        _writing = writing;

        // Preselected so the player can skip it (plan §3).
        Selected = ends[0];
    }

    public string Id { get; }

    public string Label { get; }

    public IReadOnlyList<TemperEndOption> Ends { get; }

    public string Writing => Selected is null ? "" : _writing(Selected.Id);
}

/// <summary>A look feature in the new-game form, whose options depend on the chosen subject.</summary>
public sealed partial class FeatureField(string label, string feature) : ObservableObject
{
    [ObservableProperty]
    private IReadOnlyList<string> _options = [];

    [ObservableProperty]
    private string? _value;

    public string Label { get; } = label;

    public string Feature { get; } = feature;
}

/// <summary>One exchange of a scene's conversation: what the player said or did, and how the others answered.</summary>
public sealed partial class ExchangeItem(string reply) : ObservableObject
{
    [ObservableProperty]
    private string? _reaction;

    [ObservableProperty]
    private string? _popup;

    [ObservableProperty]
    private string? _agreed;

    public string Reply { get; } = reply;
}

/// <summary>
/// One slot of the map's lineup: someone the player has met, standing on the backdrop, or an empty slot for
/// someone not met yet, so the row fills the stage once everyone is met.
/// </summary>
public sealed partial class LineupItem(string key, string name, bool isMet) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWaiting))]
    private Bitmap? _portrait;

    public string Key { get; } = key;

    public string Name { get; } = name;

    public bool IsMet { get; } = isMet;

    /// <summary>Someone met whose picture is still being drawn.</summary>
    public bool IsWaiting => IsMet && Portrait is null;
}

/// <summary>A person to pick, drawn standing on the shared backdrop; or nobody.</summary>
public sealed partial class PersonCardViewModel(string key, string label, bool isNobody, ICommand command) : ObservableObject
{
    [ObservableProperty]
    private bool _isChosen;

    [ObservableProperty]
    private Bitmap? _backdrop;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWaiting))]
    private Bitmap? _portrait;

    public string Key { get; } = key;

    /// <summary>A person whose picture is still being drawn.</summary>
    public bool IsWaiting => !IsNobody && Portrait is null;

    public string Label { get; } = label;

    public bool IsNobody { get; } = isNobody;

    public ICommand Command { get; } = command;
}

/// <summary>A place to go, with its background at this slot and weather.</summary>
public sealed partial class PlaceCardViewModel(PlaceRecord place, string typeName, ICommand command, IReadOnlyList<ChoiceItem>? activities = null)
    : ObservableObject
{
    [ObservableProperty]
    private Bitmap? _thumbnail;

    public PlaceRecord Place { get; } = place;

    /// <summary>What there is to do there besides passing time: the player's shift when it is now, then the place's activities.</summary>
    public IReadOnlyList<ChoiceItem> Activities { get; } = activities ?? [];

    public string Name => Place.Name;

    public string TypeName { get; } = typeName;

    public ICommand Command { get; } = command;
}

/// <summary>A candidate portrait for a new character's look.</summary>
public sealed partial class CandidateItem(Candidate candidate, string description, ICommand command) : ObservableObject
{
    [ObservableProperty]
    private Bitmap? _picture;

    [ObservableProperty]
    private bool _isSelected;

    public Candidate Candidate { get; } = candidate;

    public string Description { get; } = description;

    public ICommand Command { get; } = command;
}
