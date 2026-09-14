using System.Text.Json;
using System.Text.Json.Serialization;
using Game.Core.Content;

namespace Game.Core.Settings;

/// <summary>
/// Loads every <c>*.json</c> in a directory as a setting, validated against the place type catalog.
/// </summary>
/// <remarks>
/// A setting is mostly references: place types, details, place ids used by openings and events.
/// Each is checked at load, because a broken one would otherwise surface days into a playthrough,
/// when an event or opening first asks for a place that does not exist.
/// </remarks>
public sealed class JsonSettingCatalog : ISettingCatalog
{
    /// <summary>Plan §4: every setting offers exactly three openings.</summary>
    public const int OpeningCount = 3;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IReadOnlyDictionary<string, SettingDefinition> _byId;

    public JsonSettingCatalog(string directory, ILocationCatalog placeTypes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(placeTypes);

        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Settings directory '{directory}' not found.");
        }

        var byId = new Dictionary<string, SettingDefinition>(StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            var setting = JsonSerializer.Deserialize<SettingDefinition>(File.ReadAllText(path), Json)
                ?? throw new InvalidOperationException($"Setting '{path}' deserialised to null.");

            var expected = Path.GetFileNameWithoutExtension(path);
            if (!string.Equals(setting.Id, expected, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Setting '{path}' declares id '{setting.Id}' but is named '{expected}'.");
            }

            Validate(setting, placeTypes);
            byId[setting.Id] = setting;
        }

        if (byId.Count == 0)
        {
            throw new InvalidOperationException($"No settings found in '{directory}'.");
        }

        _byId = byId;
    }

    public SettingDefinition Get(string settingId) =>
        _byId.TryGetValue(settingId, out var setting)
            ? setting
            : throw new KeyNotFoundException(
                $"Unknown setting '{settingId}'. Known: {string.Join(", ", _byId.Keys.Order(StringComparer.Ordinal))}.");

    public IReadOnlyList<SettingDefinition> All() => [.. _byId.Values.OrderBy(static s => s.Id, StringComparer.Ordinal)];

    private static void Validate(SettingDefinition setting, ILocationCatalog placeTypes)
    {
        void Fail(string message) => throw new InvalidOperationException($"Setting '{setting.Id}': {message}");

        if (setting.Days < 7)
        {
            Fail($"lasts {setting.Days} days; a playthrough needs at least a week.");
        }

        if (string.IsNullOrWhiteSpace(setting.Tone))
        {
            Fail("has no tone for the writing.");
        }

        if (setting.Places.Count == 0)
        {
            Fail("has no places.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var place in setting.Places)
        {
            if (!ids.Add(place.Id))
            {
                Fail($"declares place '{place.Id}' twice.");
            }

            LocationDefinition type;
            try
            {
                type = placeTypes.Get(place.Type);
            }
            catch (KeyNotFoundException)
            {
                Fail($"place '{place.Id}' has unknown place type '{place.Type}'.");
                return;
            }

            foreach (var detail in place.Details ?? [])
            {
                if (!(type.Details ?? []).Any(d => d.Id == detail))
                {
                    Fail($"place '{place.Id}' uses detail '{detail}', which place type '{type.Id}' does not offer.");
                }
            }
        }

        bool Exists(string id) => ids.Contains(id);
        bool Known(string id) => setting.Places.Any(p => p.Id == id && p.Known);

        if (!Exists(setting.RoutinePlace) || !Known(setting.RoutinePlace))
        {
            Fail($"routine place '{setting.RoutinePlace}' must be one of its places, known from the start.");
        }

        if (setting.Openings.Count != OpeningCount)
        {
            Fail($"declares {setting.Openings.Count} openings; a setting offers exactly {OpeningCount}.");
        }

        var openingIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var opening in setting.Openings)
        {
            if (!openingIds.Add(opening.Id))
            {
                Fail($"declares opening '{opening.Id}' twice.");
            }

            if (!Exists(opening.MeetingPlace) || !Known(opening.MeetingPlace))
            {
                Fail($"opening '{opening.Id}' meets at '{opening.MeetingPlace}', which must be one of its places, known from the start.");
            }

            if (!Exists(opening.HomePlace))
            {
                Fail($"opening '{opening.Id}' has home place '{opening.HomePlace}', which is not one of its places.");
            }

            if (opening.SecondPlace is null || !Exists(opening.SecondPlace) || opening.SecondPlace == opening.HomePlace)
            {
                Fail($"opening '{opening.Id}' needs a second place, one of its places other than the home place, where a missed recognise beat re-arms.");
            }
        }

        foreach (var ev in setting.Events)
        {
            if (ev.Day < 1 || ev.Day > setting.Days)
            {
                Fail($"event '{ev.Id}' is on day {ev.Day}, outside days 1-{setting.Days}.");
            }

            if (!Exists(ev.Place))
            {
                Fail($"event '{ev.Id}' is at '{ev.Place}', which is not one of its places.");
            }
        }

        if (setting.Occupations.Count == 0)
        {
            Fail("has no occupations for its characters.");
        }
    }
}
