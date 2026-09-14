using Game.Core.Places;
using Game.Core.Saves;
using Game.Core.Settings;
using Game.Data.Repositories;
using Microsoft.Extensions.Options;

namespace Game.Host.Services;

/// <summary>A save's setting and places.</summary>
public sealed class WorldService(
    SaveRepository saves,
    PlaceRepository places,
    ISettingCatalog settings,
    IOptions<StudioOptions> options)
{
    /// <summary>
    /// The places the player can choose between, after making sure the save has its setting's
    /// authored places. A save created before settings existed is given the default setting, once.
    /// </summary>
    public async Task<IReadOnlyList<PlaceRecord>> KnownPlacesAsync(SaveId saveId, CancellationToken ct = default)
    {
        var settingId = await saves.GetSettingIdAsync(saveId, ct).ConfigureAwait(false)
            ?? await saves.SetSettingAsync(saveId, options.Value.DefaultSettingId, ct).ConfigureAwait(false);

        await places.AddAsync(PlaceRecord.Authored(saveId, settings.Get(settingId)), ct).ConfigureAwait(false);

        var known = await places.ListAsync(saveId, knownOnly: true, ct).ConfigureAwait(false);

        return known.Count > 0
            ? known
            : throw new InvalidOperationException($"Save '{saveId}' has no known places in setting '{settingId}'.");
    }
}
