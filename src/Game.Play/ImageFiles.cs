using Game.Imaging.Caching;
using Microsoft.Extensions.Options;

namespace Game.Play;

/// <summary>
/// Turns an image's store path (<c>img/{hash}.png</c>, what the web host serves) into the file on
/// disk, for frontends that draw images themselves instead of asking a browser to fetch them.
/// </summary>
public sealed class ImageFiles(IOptions<ImageStoreOptions> options)
{
    private readonly string _root = Path.GetFullPath(options.Value.Directory);

    /// <summary>The full path of a stored image, or null for a path that is not a stored image.</summary>
    public string? PathOf(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        // Only the file name is kept: a stored path is always the prefix plus one content-addressed
        // name, and dropping everything before it keeps a stray path from leaving the store.
        var name = Path.GetFileName(relativePath);
        var hash = Path.GetFileNameWithoutExtension(name);
        return ContentAddress.IsWellFormed(hash) ? Path.Combine(_root, name) : null;
    }
}
