using System.Text.Json;

namespace Game.Core.Content;

/// <summary>Loads locations from a single JSON file at construction and holds them in memory.</summary>
public sealed class JsonLocationCatalog : ILocationCatalog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IReadOnlyDictionary<string, LocationDefinition> _byId;

    public JsonLocationCatalog(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Location catalog '{path}' not found.", path);
        }

        var definitions = JsonSerializer.Deserialize<List<LocationDefinition>>(File.ReadAllText(path), Json)
            ?? throw new InvalidOperationException($"Location catalog '{path}' deserialised to null.");

        _byId = definitions.ToDictionary(static d => d.Id, StringComparer.Ordinal);
    }

    public LocationDefinition Get(string locationId) =>
        _byId.TryGetValue(locationId, out var definition)
            ? definition
            : throw new KeyNotFoundException(
                $"Unknown location '{locationId}'. Known: {string.Join(", ", _byId.Keys.Order(StringComparer.Ordinal))}.");

    /// <summary>Ordered by id so callers and tests never depend on dictionary iteration order.</summary>
    public IReadOnlyList<LocationDefinition> All() =>
        [.. _byId.Values.OrderBy(static d => d.Id, StringComparer.Ordinal)];
}
