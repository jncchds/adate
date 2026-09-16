using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Game.Core;
using Game.Core.Cast;
using Game.Core.Characters;
using Game.Core.Settings;
using Game.Core.Style;
using Game.Data.Repositories;
using Game.Play;

namespace Game.App.ViewModels;

/// <summary>One content level in the list, shown the way people write it.</summary>
public sealed record ContentChoice(Ceiling Level)
{
    public override string ToString() => Level switch
    {
        Ceiling.PG13 => "PG-13",
        Ceiling.Suggestive => "Suggestive",
        _ => "Explicit",
    };
}

/// <summary>Where the story happens, who the player is, and who they will meet.</summary>
public sealed partial class NewGameViewModel : PageViewModel
{
    private readonly MainViewModel _main;
    private readonly GameServices _services;
    private readonly CharacterStudio _studio;
    private StylePack? _pack;

    [ObservableProperty]
    private SettingItem? _setting;

    [ObservableProperty]
    private string _playerName = "";

    [ObservableProperty]
    private string? _playerGender = "woman";

    [ObservableProperty]
    private string _narrationLanguage = Game.Core.Story.NarrationLanguage.Default;

    [ObservableProperty]
    private string _visualStyle = Game.Core.Style.VisualStyle.LabelFor(Game.Core.Style.VisualStyle.Default);

    [ObservableProperty]
    private ContentChoice _contentLevel = new(Ceiling.PG13);

    [ObservableProperty]
    private string _liName = "";

    [ObservableProperty]
    private IReadOnlyList<string> _subjects = ["female"];

    [ObservableProperty]
    private string? _subject = "female";

    [ObservableProperty]
    private decimal? _age = 24;

    [ObservableProperty]
    private string _distinguishingFeature = "";

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    [NotifyPropertyChangedFor(nameof(CreateLabel))]
    private bool _working;

    public NewGameViewModel(MainViewModel main, GameServices services)
    {
        _main = main;
        _services = services;
        _studio = services.Get<CharacterStudio>();

        Settings = [.. services.Get<ISettingCatalog>().All().Select(s => new SettingItem(s.Id, s.DisplayName, $"{s.Days} days. {s.Tone}"))];
        _setting = Settings.Count > 0 ? Settings[0] : null;

        Features =
        [
            new FeatureField("Hair colour", AppearanceFeatures.HairColor),
            new FeatureField("Hair style", AppearanceFeatures.HairStyle),
            new FeatureField("Eye colour", AppearanceFeatures.EyeColor),
            new FeatureField("Skin tone", AppearanceFeatures.SkinTone),
            new FeatureField("Build", AppearanceFeatures.Build),
            new FeatureField("Height", AppearanceFeatures.Height),
        ];

        Temper =
        [
            .. services.Get<CastContent>().Temper.Select(axis => new TemperAxisItem(
                axis.Id,
                axis.Label,
                [.. axis.Ends.Select(end => new TemperEndOption(end.Id, end.Label))],
                id => axis.End(id).Writing)),
        ];
    }

    public IReadOnlyList<SettingItem> Settings { get; }

    public IReadOnlyList<string> Genders { get; } = ["woman", "man", "nonbinary"];

    public IReadOnlyList<string> VisualStyles { get; } = [.. Game.Core.Style.VisualStyle.Presets.Select(p => p.Label)];

    public IReadOnlyList<ContentChoice> ContentLevels { get; } =
        [.. new[] { Ceiling.PG13, Ceiling.Suggestive, Ceiling.Explicit }.Select(c => new ContentChoice(c))];

    public IReadOnlyList<FeatureField> Features { get; }

    public IReadOnlyList<TemperAxisItem> Temper { get; }

    public string CreateLabel => Working ? "Creating…" : $"Generate {_studio.CandidateCount} portraits";

    public override async Task LoadAsync()
    {
        try
        {
            _pack = await _studio.GetPackAsync();
            Subjects = [.. _pack.Subjects.Keys.Order(StringComparer.Ordinal)];
            Subject = Subjects[0];
            ApplySubjectChoices();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    partial void OnSubjectChanged(string? value) => ApplySubjectChoices();

    /// <summary>
    /// Choices are per subject, so switching subject can leave a value the new one does not offer.
    /// Each current value is kept when it is still on offer and otherwise replaced by the first choice.
    /// </summary>
    private void ApplySubjectChoices()
    {
        if (_pack is null || Subject is null)
        {
            return;
        }

        foreach (var field in Features)
        {
            IReadOnlyList<string> options = [.. _pack.SubjectFor(Subject).OptionsFor(field.Feature).Select(o => o.Tag)];

            // Decided before the list changes: replacing a combo box's items clears its selection.
            var keep = field.Value is not null && options.Contains(field.Value, StringComparer.OrdinalIgnoreCase)
                ? field.Value
                : options.Count > 0 ? options[0] : field.Value;

            field.Options = options;
            field.Value = keep;
        }
    }

    private bool CanCreate() => !Working;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        Working = true;
        Error = null;

        try
        {
            var playerName = Name(PlayerName, "your name");
            var liName = Name(LiName, "their name");

            string Feature(string feature) => Features.First(f => f.Feature == feature).Value ?? "";

            var appearance = new CharacterAppearance(
                Subject ?? "female",
                (int)(Age ?? 0),
                Feature(AppearanceFeatures.EyeColor),
                Feature(AppearanceFeatures.HairColor),
                Feature(AppearanceFeatures.HairStyle),
                Feature(AppearanceFeatures.SkinTone),
                Feature(AppearanceFeatures.Build),
                Feature(AppearanceFeatures.Height),
                DistinguishingFeature);
            appearance.Validate();

            var visualStyle = Game.Core.Style.VisualStyle.Normalize(VisualStyle, _pack ?? await _studio.GetPackAsync());

            var save = await _services.Get<SaveRepository>().CreateAsync(
                _studio.StylePackId,
                _studio.PackFingerprint(),
                ContentLevel.Level,
                Setting?.Id ?? throw new ArgumentException("Choose a setting."),
                playerName,
                PlayerGender ?? "woman",
                Game.Core.Story.NarrationLanguage.Normalize(NarrationLanguage),
                visualStyle);

            var temper = Temper.ToDictionary(t => t.Id, t => t.Selected?.Id ?? t.Ends[0].Id, StringComparer.Ordinal);
            var character = await _services.Get<CharacterRepository>().CreateAsync(save.Id, appearance, liName, temper);

            _main.ShowCandidates(character.Id);
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
    private void Back() => _main.ShowHome();

    private static string Name(string? value, string what)
    {
        var trimmed = value?.Trim() ?? "";

        return trimmed.Length is > 0 and <= 40
            ? trimmed
            : throw new ArgumentException($"Enter {what}, up to 40 characters.");
    }
}
