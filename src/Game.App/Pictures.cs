using System.Collections.Concurrent;
using Avalonia.Media.Imaging;

namespace Game.App;

/// <summary>
/// Decodes stored images off the UI thread and keeps recent ones. Images are content-addressed, so
/// a path always holds the same picture and a cached bitmap never goes stale.
/// </summary>
public static class Pictures
{
    /// <summary>Beyond this many the cache starts over; bitmaps still on screen stay alive regardless.</summary>
    private const int Keep = 96;

    private static readonly ConcurrentDictionary<(string Path, int Width), Task<Bitmap?>> Cache = new();

    /// <param name="path">The image file, or null when there is none yet.</param>
    /// <param name="decodeWidth">Decode to this width to save memory on thumbnails; 0 for full size.</param>
    public static Task<Bitmap?> LoadAsync(string? path, int decodeWidth = 0)
    {
        if (path is null || !File.Exists(path))
        {
            return Task.FromResult<Bitmap?>(null);
        }

        if (Cache.Count > Keep)
        {
            Cache.Clear();
        }

        return Cache.GetOrAdd((path, decodeWidth), static key => Task.Run(() => Decode(key.Path, key.Width)));
    }

    private static Bitmap? Decode(string path, int width)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return width > 0 ? Bitmap.DecodeToWidth(stream, width) : new Bitmap(stream);
        }
        catch (Exception)
        {
            // A picture that cannot be read leaves its place empty; the game goes on without it.
            return null;
        }
    }
}
