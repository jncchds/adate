namespace Game.Imaging;

/// <summary>
/// The result of a generation, already persisted to the content-addressed store.
/// </summary>
/// <param name="Hash">sha256 of the generation parameters. Also the filename stem.</param>
/// <param name="RelativePath">Path under the image root, e.g. <c>img/ab12....png</c>.</param>
/// <param name="FromCache">True if no GPU work happened. Useful for the spike's timing criteria.</param>
public sealed record GeneratedImage(
    string Hash,
    string RelativePath,
    int Width,
    int Height,
    bool FromCache);
