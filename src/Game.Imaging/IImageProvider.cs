namespace Game.Imaging;

/// <summary>
/// The only way the rest of the system asks for art. Implementations are responsible for
/// cache lookup, GPU leasing and persistence; callers just describe what they want.
/// </summary>
public interface IImageProvider
{
    Task<GeneratedImage> GenerateAsync(ImageRequest req, CancellationToken ct = default);
}
