namespace Game.Imaging.Caching;

/// <summary>
/// The content-addressed image store (HANDOFF 1.7). Files are named by the hash of the
/// parameters that produced them, which makes them immutable: a given name can only ever
/// hold one possible image, so they are safe to serve with long-lived cache headers.
/// </summary>
public interface IImageStore
{
    bool Exists(string hash);

    /// <summary>Path relative to the web root, suitable for an <c>img src</c>.</summary>
    string RelativePath(string hash);

    Task<byte[]> ReadAsync(string hash, CancellationToken ct = default);

    Task SaveAsync(string hash, ReadOnlyMemory<byte> content, CancellationToken ct = default);
}
