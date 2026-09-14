using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Game.Core.Settings;

namespace Game.Core.Encounters;

/// <summary>
/// Loads encounters from <c>{directory}/common.json</c> and <c>{directory}/{setting}.json</c>, adds
/// one encounter per dated setting event, and validates every setting's set at construction.
/// </summary>
/// <remarks>
/// Encounters are references to places, flags and days. A broken one would surface as a beat that
/// silently never fires, which is invisible in play, so each is refused at load with its reference
/// named. A file that matches no setting is refused too: a typo in a file name would otherwise
/// drop a setting's encounters without a word.
/// </remarks>
public sealed partial class JsonEncounterCatalog : IEncounterCatalog
{
    public const string CommonFileName = "common";

    /// <summary>Priority given to dated setting events, above any authored encounter's default.</summary>
    public const int EventPriority = 100;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IReadOnlyDictionary<string, IReadOnlyList<EncounterDefinition>> _bySetting;

    public JsonEncounterCatalog(string directory, ISettingCatalog settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(settings);

        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Encounters directory '{directory}' not found.");
        }

        var known = settings.All().Select(s => s.Id).Append(CommonFileName).ToHashSet(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!known.Contains(name))
            {
                throw new InvalidOperationException(
                    $"Encounter file '{file}' matches no setting. Name it after a setting id or '{CommonFileName}'.");
            }
        }

        var common = Read(Path.Combine(directory, CommonFileName + ".json"));
        var bySetting = new Dictionary<string, IReadOnlyList<EncounterDefinition>>(StringComparer.Ordinal);

        foreach (var setting in settings.All())
        {
            IReadOnlyList<EncounterDefinition> all =
            [
                .. common,
                .. Read(Path.Combine(directory, setting.Id + ".json")),
                .. setting.Events.Select(Event),
            ];

            Validate(setting, all);
            bySetting[setting.Id] = all;
        }

        _bySetting = bySetting;
    }

    public IReadOnlyList<EncounterDefinition> For(string settingId) =>
        _bySetting.TryGetValue(settingId, out var encounters)
            ? encounters
            : throw new KeyNotFoundException($"No encounters are loaded for setting '{settingId}'.");

    private static EncounterDefinition Event(SettingEvent ev) => new(
        $"event.{ev.Id}",
        new EncounterPlace(Id: ev.Place),
        Time: [ev.Time],
        Days: [ev.Day, ev.Day],
        Priority: EventPriority,
        Text: $"{ev.Name} is happening here today. (Placeholder: the event scene.)");

    private static void Validate(SettingDefinition setting, IReadOnlyList<EncounterDefinition> encounters)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var places = setting.Places.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var encounter in encounters)
        {
            void Fail(string message) =>
                throw new InvalidOperationException($"Encounter '{encounter.Id}' in setting '{setting.Id}': {message}");

            if (string.IsNullOrWhiteSpace(encounter.Id) || !ids.Add(encounter.Id))
            {
                Fail("has a blank or duplicate id.");
            }

            if (encounter.Place.Id is { } place && !places.Contains(place))
            {
                Fail($"happens at '{place}', which is not one of the setting's places.");
            }

            if (encounter.Place.PlaceFlag is { } placeFlag && !FlagKey().IsMatch(placeFlag))
            {
                Fail($"reads its place from '{placeFlag}', which is not a valid flag key.");
            }

            if (encounter.Place.AloneVisitsBefore is < 1)
            {
                Fail("needs at least one earlier solo visit, or no solo-visit condition.");
            }

            if (encounter.Days is { } days &&
                (days.Count != 2 || days[0] < 1 || days[0] > days[1] || days[1] > setting.Days))
            {
                Fail($"has days [{string.Join(", ", days)}]; it needs [first, last] within days 1-{setting.Days}.");
            }

            foreach (var expression in encounter.Requires ?? [])
            {
                if (!FlagExpression().IsMatch(expression))
                {
                    Fail($"requires '{expression}', which is not key, !key or key=value.");
                }
            }

            foreach (var set in encounter.Sets ?? [])
            {
                if (!FlagAssignment().IsMatch(set))
                {
                    Fail($"sets '{set}', which is not key or key=value.");
                }
            }

            foreach (var reveal in encounter.Reveals ?? [])
            {
                if (!places.Contains(reveal))
                {
                    Fail($"reveals '{reveal}', which is not one of the setting's places.");
                }
            }

            foreach (var who in encounter.With ?? [])
            {
                if (!WithRef().IsMatch(who))
                {
                    Fail($"is with '{who}'; use main_li or variant:{{route}}.");
                }
            }

            if (string.IsNullOrWhiteSpace(encounter.Text))
            {
                Fail("has no text.");
            }
        }
    }

    private static IReadOnlyList<EncounterDefinition> Read(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<List<EncounterDefinition>>(File.ReadAllText(path), Json)
              ?? throw new InvalidOperationException($"Encounter file '{path}' deserialised to null.")
            : [];

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._:-]*$")]
    private static partial Regex FlagKey();

    [GeneratedRegex(@"^!?[a-z0-9][a-z0-9._:-]*(=[^=\s]+)?$")]
    private static partial Regex FlagExpression();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._:-]*(=[^=\s]+)?$")]
    private static partial Regex FlagAssignment();

    [GeneratedRegex(@"^(main_li|variant:[a-z0-9-]+)$")]
    private static partial Regex WithRef();
}
