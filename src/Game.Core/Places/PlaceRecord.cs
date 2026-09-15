using Game.Core.Saves;
using Game.Core.Settings;

namespace Game.Core.Places;

public enum PlaceOrigin
{
    /// <summary>Written into the setting.</summary>
    Authored,

    /// <summary>Introduced by the story during play (plan §10).</summary>
    Story,
}

/// <summary>
/// A place in one save. Mirrors the <c>place</c> table.
/// </summary>
/// <param name="Id">Unique within the save. Backgrounds are cached against it.</param>
/// <param name="TypeId">The place type that supplies its prompt vocabulary.</param>
/// <param name="Name">Display text only. Never enters a prompt.</param>
/// <param name="Details">Detail ids from the place type.</param>
/// <param name="Seed">
/// The place's own seed, the same at every time of day, so morning and night show the same room.
/// Stored rather than derived at render time: the previous background seed came from
/// <see cref="string.GetHashCode()"/>, which is randomised per process, so every restart
/// regenerated every background.
/// </param>
/// <param name="Known">Whether the player can choose to go there.</param>
/// <param name="Look">How a story place looks, in the writer's words, added to its type's description; null for authored places.</param>
public sealed record PlaceRecord(
    SaveId SaveId,
    string Id,
    string TypeId,
    string Name,
    IReadOnlyList<string> Details,
    long Seed,
    PlaceOrigin Origin,
    bool Known,
    int? FirstDay,
    string? Look = null)
{
    /// <summary>The setting's authored places for a save, in setting order.</summary>
    public static IReadOnlyList<PlaceRecord> Authored(SaveId saveId, SettingDefinition setting)
    {
        ArgumentNullException.ThrowIfNull(setting);

        return
        [
            .. setting.Places.Select(p => new PlaceRecord(
                saveId,
                p.Id,
                p.Type,
                p.Name,
                p.Details ?? [],
                SeedFor(saveId, p.Id),
                PlaceOrigin.Authored,
                p.Known,
                p.Known ? 1 : null)),
        ];
    }

    /// <summary>FNV-1a over the save and place ids, reduced to a non-negative seed any provider accepts.</summary>
    public static long SeedFor(SaveId saveId, string placeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(placeId);

        const ulong prime = 1099511628211UL;
        var hash = 14695981039346656037UL;

        foreach (var ch in $"{saveId}|{placeId}")
        {
            hash = unchecked((hash ^ ch) * prime);
        }

        return (long)(hash % int.MaxValue);
    }
}
