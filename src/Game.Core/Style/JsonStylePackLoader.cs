using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Game.Core.Style;

/// <summary>
/// Reads pack manifests from a directory of JSON files. Spike 0 ships exactly one pack and
/// hardcodes the selection, but reaches it through this loader so adding packs later touches
/// no call sites (HANDOFF 1.5).
/// </summary>
public sealed class JsonStylePackLoader : IStylePackLoader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ConcurrentDictionary<string, StylePack> _cache = new(StringComparer.Ordinal);
    private readonly string _root;

    public JsonStylePackLoader(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _root = Path.GetFullPath(directory);
    }

    /// <summary>
    /// The manifest's raw text, hashed to produce the fingerprint that enters the image cache
    /// key. Exposed so a caller can bind generated art to the exact pack that produced it.
    /// </summary>
    public string ReadManifestText(string packId) => File.ReadAllText(PathFor(packId));

    public Task<StylePack> LoadAsync(string packId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);

        if (_cache.TryGetValue(packId, out var cached))
        {
            return Task.FromResult(cached);
        }

        var path = PathFor(packId);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Style pack '{packId}' not found at '{path}'.", path);
        }

        var pack = JsonSerializer.Deserialize<StylePack>(File.ReadAllText(path), Json)
            ?? throw new InvalidOperationException($"Style pack '{path}' deserialised to null.");

        if (!string.Equals(pack.Id, packId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Style pack '{path}' declares id '{pack.Id}' but was loaded as '{packId}'.");
        }

        if (pack.SupportedCeilings.Count == 0)
        {
            throw new InvalidOperationException(
                $"Style pack '{packId}' supports no content ceilings, so it can never render anything.");
        }

        if (pack.Subjects.Count == 0)
        {
            throw new InvalidOperationException(
                $"Style pack '{packId}' declares no subjects, so it cannot render a character. " +
                "Every pack needs at least one entry under 'subjects'.");
        }

        foreach (var (subject, profile) in pack.Subjects)
        {
            if (profile.Positive.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Subject '{subject}' in style pack '{packId}' has no positive tags. " +
                    "Without them the checkpoint picks a subject on its own, which is the " +
                    "bug this block exists to prevent.");
            }
        }

        foreach (var (subject, profile) in pack.Subjects)
        {
            if (profile.Outfit.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Subject '{subject}' in style pack '{packId}' has no default outfit. " +
                    "An unspecified outfit is re-invented on every render, which moves the " +
                    "silhouette between expressions and breaks the crossfade.");
            }
        }

        if (pack.AlwaysNegative.Count == 0)
        {
            throw new InvalidOperationException(
                $"Style pack '{packId}' declares no alwaysNegative terms. Those are the " +
                "protections that hold at every ceiling, so a pack without them is not one " +
                "that can be loaded.");
        }

        if (pack.Expressions.Count == 0)
        {
            throw new InvalidOperationException(
                $"Style pack '{packId}' defines no expressions. Slot names are not usable tags " +
                "on their own, so the pack has to supply the phrasing for each one.");
        }

        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_cache.GetOrAdd(packId, pack));
    }

    public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!Directory.Exists(_root))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        IReadOnlyList<string> ids =
        [
            .. Directory.EnumerateFiles(_root, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .Order(StringComparer.Ordinal),
        ];

        return Task.FromResult(ids);
    }

    private string PathFor(string packId) => Path.Combine(_root, $"{packId}.json");
}
