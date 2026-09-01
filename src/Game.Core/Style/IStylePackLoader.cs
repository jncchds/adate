namespace Game.Core.Style;

/// <summary>
/// Reads pack manifests. Spike 0 ships exactly one pack and hardcodes the selection,
/// but reaches it through this loader so adding packs later touches no call sites.
/// </summary>
public interface IStylePackLoader
{
    Task<StylePack> LoadAsync(string packId, CancellationToken ct = default);

    Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default);
}
