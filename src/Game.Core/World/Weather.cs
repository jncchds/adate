using System.Text.Json;
using Game.Core.Content;
using Game.Core.Settings;

namespace Game.Core.World;

/// <param name="Writing">How the scene writer is told about it. Never enters an image prompt.</param>
/// <param name="Weight">How often it comes up in a setting that declares no weights of its own.</param>
public sealed record WeatherDefinition(string Id, string Label, string Writing, int Weight);

/// <summary>
/// The weather kinds (phase-3 plan: weather in the writing and the backgrounds). How each looks is
/// place-type vocabulary, per indoor and outdoor kind; this file holds what they are and how often.
/// </summary>
public sealed record WeatherContent(IReadOnlyList<WeatherDefinition> Kinds)
{
    /// <summary>Adds nothing to a background prompt, so backgrounds drawn before weather existed stay valid.</summary>
    public const string Clear = "clear";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static WeatherContent Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Weather content '{path}' not found.", path);
        }

        var content = new WeatherContent(
            JsonSerializer.Deserialize<List<WeatherDefinition>>(File.ReadAllText(path), Json)
            ?? throw new InvalidOperationException($"Weather content '{path}' deserialised to null."));

        content.Validate();
        return content;
    }

    public WeatherDefinition Get(string id) =>
        Kinds.FirstOrDefault(k => string.Equals(k.Id, id, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"Unknown weather '{id}'. Known: {string.Join(", ", Kinds.Select(k => k.Id))}.");

    public void Validate()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kind in Kinds)
        {
            if (string.IsNullOrWhiteSpace(kind.Id) || !ids.Add(kind.Id))
            {
                throw new InvalidOperationException($"Weather id '{kind.Id}' is blank or declared twice.");
            }

            if (string.IsNullOrWhiteSpace(kind.Label) || string.IsNullOrWhiteSpace(kind.Writing) || kind.Weight < 0)
            {
                throw new InvalidOperationException($"Weather '{kind.Id}' needs a label, writing and a weight of zero or more.");
            }
        }

        if (!ids.Contains(Clear) || Kinds.All(k => k.Weight == 0))
        {
            throw new InvalidOperationException($"Weather content needs a '{Clear}' kind and at least one kind with a weight.");
        }
    }

    /// <summary>Every place type can draw every kind, and every setting's weights name real kinds.</summary>
    public void ValidateAgainst(ILocationCatalog locations, ISettingCatalog settings)
    {
        ArgumentNullException.ThrowIfNull(locations);
        ArgumentNullException.ThrowIfNull(settings);

        foreach (var location in locations.All())
        {
            foreach (var kind in Kinds)
            {
                if (location.WeatherTags?.ContainsKey(kind.Id) is not true || location.WeatherDescriptions?.ContainsKey(kind.Id) is not true)
                {
                    throw new InvalidOperationException($"Place type '{location.Id}' has no look for weather '{kind.Id}'.");
                }
            }
        }

        foreach (var setting in settings.All())
        {
            if (setting.Weather is null)
            {
                continue;
            }

            foreach (var (id, weight) in setting.Weather)
            {
                if (Kinds.All(k => k.Id != id) || weight < 0)
                {
                    throw new InvalidOperationException($"Setting '{setting.Id}' weights weather '{id}' at {weight}; it must be a known kind and zero or more.");
                }
            }

            if (setting.Weather.Values.All(w => w == 0))
            {
                throw new InvalidOperationException($"Setting '{setting.Id}' gives every weather a weight of zero.");
            }
        }
    }
}

/// <summary>
/// The day's weather, deterministic from the save and the day so nothing is stored. Weather tends to
/// hold: each day keeps yesterday's with <see cref="KeepChance"/>, otherwise it is drawn again.
/// </summary>
public static class WeatherRoll
{
    public const double KeepChance = 0.5;

    public static string For(string saveKey, int day, WeatherContent content, SettingDefinition setting)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(saveKey);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(setting);
        ArgumentOutOfRangeException.ThrowIfLessThan(day, 1);

        var weights = content.Kinds
            .Select(k => (k.Id, Weight: setting.Weather is null ? k.Weight : setting.Weather.GetValueOrDefault(k.Id)))
            .Where(w => w.Weight > 0)
            .ToList();

        var today = Pick(weights, Unit(saveKey, 1, "pick"));
        for (var d = 2; d <= day; d++)
        {
            if (Unit(saveKey, d, "keep") >= KeepChance)
            {
                today = Pick(weights, Unit(saveKey, d, "pick"));
            }
        }

        return today;
    }

    private static string Pick(IReadOnlyList<(string Id, int Weight)> weights, double unit)
    {
        var x = unit * weights.Sum(w => w.Weight);
        foreach (var (id, weight) in weights)
        {
            if (x < weight)
            {
                return id;
            }

            x -= weight;
        }

        return weights[^1].Id;
    }

    /// <summary>FNV-1a, then a SplitMix64 finaliser, mapped onto [0, 1).</summary>
    private static double Unit(string key, int day, string salt)
    {
        var hash = 14695981039346656037UL;
        foreach (var ch in $"{key}|{day}|{salt}")
        {
            hash = unchecked((hash ^ ch) * 1099511628211UL);
        }

        hash = (hash ^ (hash >> 30)) * 0xBF58476D1CE4E5B9UL;
        hash = (hash ^ (hash >> 27)) * 0x94D049BB133111EBUL;
        hash ^= hash >> 31;

        return (hash >> 11) * (1.0 / (1UL << 53));
    }
}
