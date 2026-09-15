using System.Text.Json;
using System.Text.Json.Serialization;
using Game.Core.Scenes;
using Game.Core.World;

namespace Game.Core.Story;

/// <summary>A small thing going on at a place, for a scene to write around; limited to some times of day when it says so.</summary>
public sealed record Happening(string Text, IReadOnlyList<TimeOfDay>? Slots = null);

/// <summary>
/// Small happenings per place type and per weather (quality material, user request): an ordinary scene with
/// nothing going on was written as atmosphere and nothing else. C# picks one, the same for the same save,
/// place and slot, and the writer builds the moment around it.
/// </summary>
public sealed record HappeningContent(
    IReadOnlyDictionary<string, IReadOnlyList<Happening>> Places,
    IReadOnlyDictionary<string, IReadOnlyList<Happening>> Weather)
{
    /// <summary>How often an ordinary scene gets a happening at all, so not every moment has an incident.</summary>
    public const double Chance = 0.75;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static HappeningContent Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var content = JsonSerializer.Deserialize<HappeningContent>(File.ReadAllText(path), Json)
            ?? throw new InvalidOperationException($"Happenings file '{path}' deserialised to null.");

        foreach (var (key, list) in content.Places.Concat(content.Weather))
        {
            if (list.Count == 0 || list.Any(h => string.IsNullOrWhiteSpace(h.Text)))
            {
                throw new InvalidOperationException($"Happenings for '{key}' are empty or have a blank text.");
            }
        }

        return content;
    }

    /// <summary>Refuses happenings for a place type or weather that does not exist, which would silently never be used.</summary>
    public void ValidateAgainst(IEnumerable<string> placeTypeIds, IEnumerable<string> weatherIds)
    {
        ArgumentNullException.ThrowIfNull(placeTypeIds);
        ArgumentNullException.ThrowIfNull(weatherIds);

        var types = placeTypeIds.ToHashSet(StringComparer.Ordinal);
        var weathers = weatherIds.ToHashSet(StringComparer.Ordinal);

        if (Places.Keys.FirstOrDefault(k => !types.Contains(k)) is { } unknownType)
        {
            throw new InvalidOperationException($"Happenings name place type '{unknownType}', which does not exist.");
        }

        if (Weather.Keys.FirstOrDefault(k => !weathers.Contains(k)) is { } unknownWeather)
        {
            throw new InvalidOperationException($"Happenings name weather '{unknownWeather}', which does not exist.");
        }
    }

    /// <summary>
    /// A happening for this place type, weather and slot, or null for a quiet moment: mostly the place's own,
    /// sometimes the weather's, never the same roll twice for different slots.
    /// </summary>
    public string? Pick(string saveKey, string placeTypeId, string? weatherId, ClockState clock)
    {
        var roll = $"{saveKey}|{placeTypeId}|{clock.Day}|{clock.Slot}|happening";
        if (Initiative.Unit(roll) >= Chance)
        {
            return null;
        }

        bool Fits(Happening h) => h.Slots is not { Count: > 0 } slots || slots.Contains(clock.Slot);

        var own = Places.GetValueOrDefault(placeTypeId) ?? [];
        var weather = weatherId is null ? [] : Weather.GetValueOrDefault(weatherId) ?? [];
        List<Happening> candidates = [.. own.Where(Fits), .. own.Where(Fits), .. own.Where(Fits), .. weather.Where(Fits)];

        return candidates.Count == 0
            ? null
            : candidates[Math.Min(candidates.Count - 1, (int)(Initiative.Unit(roll + "|which") * candidates.Count))].Text;
    }
}
