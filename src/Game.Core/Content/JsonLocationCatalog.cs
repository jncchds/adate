using System.Text.Json;
using Game.Core.Scenes;

namespace Game.Core.Content;

/// <summary>
/// Loads place types from one JSON file at construction and holds them in memory.
/// </summary>
/// <remarks>
/// Time-of-day lighting is shared by kind (indoor, outdoor) and may be overridden per type, so
/// twenty place types do not each repeat five lighting lines. Every type is checked at load to
/// cover every <see cref="TimeOfDay"/> in both vocabularies: a gap would otherwise surface as an
/// ambiguously lit background, cached for the life of a save.
/// </remarks>
public sealed class JsonLocationCatalog : ILocationCatalog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IReadOnlyDictionary<string, LocationDefinition> _byId;

    public JsonLocationCatalog(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Place type catalog '{path}' not found.", path);
        }

        var document = JsonSerializer.Deserialize<CatalogDocument>(File.ReadAllText(path), Json)
            ?? throw new InvalidOperationException($"Place type catalog '{path}' deserialised to null.");

        var byId = new Dictionary<string, LocationDefinition>(StringComparer.Ordinal);
        foreach (var entry in document.Types)
        {
            if (!byId.TryAdd(entry.Id, Build(entry, document)))
            {
                throw new InvalidOperationException($"Place type '{entry.Id}' is declared twice.");
            }
        }

        _byId = byId;
    }

    public LocationDefinition Get(string locationId) =>
        _byId.TryGetValue(locationId, out var definition)
            ? definition
            : throw new KeyNotFoundException(
                $"Unknown location '{locationId}'. Known: {string.Join(", ", _byId.Keys.Order(StringComparer.Ordinal))}.");

    /// <summary>Ordered by id so callers and tests never depend on dictionary iteration order.</summary>
    public IReadOnlyList<LocationDefinition> All() =>
        [.. _byId.Values.OrderBy(static d => d.Id, StringComparer.Ordinal)];

    private static LocationDefinition Build(TypeEntry entry, CatalogDocument document)
    {
        void Fail(string message) => throw new InvalidOperationException($"Place type '{entry.Id}': {message}");

        if (entry.Tags.Count == 0)
        {
            Fail("has no tags.");
        }

        if (string.IsNullOrWhiteSpace(entry.Description))
        {
            Fail("has no description.");
        }

        if (!document.Defaults.TryGetValue(entry.Kind, out var defaults))
        {
            Fail($"has unknown kind '{entry.Kind}'. Known: {string.Join(", ", document.Defaults.Keys)}.");
        }

        var timeTags = new Dictionary<string, IReadOnlyList<string>>(defaults!.TimeTags, StringComparer.Ordinal);
        foreach (var (time, tags) in entry.TimeTags ?? new Dictionary<string, IReadOnlyList<string>>())
        {
            timeTags[time] = tags;
        }

        var timeDescriptions = new Dictionary<string, string>(defaults.TimeDescriptions, StringComparer.Ordinal);
        foreach (var (time, description) in entry.TimeDescriptions ?? new Dictionary<string, string>())
        {
            timeDescriptions[time] = description;
        }

        foreach (var time in Enum.GetValues<TimeOfDay>())
        {
            if (!timeTags.ContainsKey(time.ToString()) || !timeDescriptions.ContainsKey(time.ToString()))
            {
                Fail($"has no lighting for {time}.");
            }
        }

        var detailIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var detail in entry.Details ?? [])
        {
            if (!detailIds.Add(detail.Id))
            {
                Fail($"declares detail '{detail.Id}' twice.");
            }

            if (detail.Tags.Count == 0 || string.IsNullOrWhiteSpace(detail.Phrase))
            {
                Fail($"detail '{detail.Id}' needs both tags and a phrase, one per prompt dialect.");
            }
        }

        return new LocationDefinition(
            entry.Id, entry.DisplayName, entry.Tags, timeTags, entry.Description, timeDescriptions, entry.Details ?? []);
    }

    private sealed record CatalogDocument(
        IReadOnlyDictionary<string, TimeDefaults> Defaults,
        IReadOnlyList<TypeEntry> Types);

    private sealed record TimeDefaults(
        IReadOnlyDictionary<string, IReadOnlyList<string>> TimeTags,
        IReadOnlyDictionary<string, string> TimeDescriptions);

    private sealed record TypeEntry(
        string Id,
        string DisplayName,
        string Kind,
        IReadOnlyList<string> Tags,
        string Description,
        IReadOnlyList<PlaceDetail>? Details = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? TimeTags = null,
        IReadOnlyDictionary<string, string>? TimeDescriptions = null);
}
