using Microsoft.Extensions.Options;

namespace Game.Imaging.Caching;

/// <summary>
/// Stores images as <c>{root}/{hash}.png</c>, served from <c>wwwroot/img/</c>.
/// </summary>
public sealed class FileImageStore : IImageStore
{
    private readonly string _root;
    private readonly string _relativePrefix;

    public FileImageStore(IOptions<ImageStoreOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _root = Path.GetFullPath(options.Value.Directory);
        _relativePrefix = options.Value.RelativePrefix.Trim('/');
        Directory.CreateDirectory(_root);
    }

    public bool Exists(string hash) => File.Exists(PathFor(hash));

    public string RelativePath(string hash) => $"{_relativePrefix}/{Guard(hash)}.png";

    public Task<byte[]> ReadAsync(string hash, CancellationToken ct = default) =>
        File.ReadAllBytesAsync(PathFor(hash), ct);

    public async Task SaveAsync(string hash, ReadOnlyMemory<byte> content, CancellationToken ct = default)
    {
        var final = PathFor(hash);

        // Write to a temporary name and move into place. A reader that finds the final name
        // must find a complete file: a half-written PNG would be cached as if it were valid
        // and, being content-addressed, would never be regenerated.
        var temp = Path.Combine(_root, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temp, content, ct).ConfigureAwait(false);
            File.Move(temp, final, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private string PathFor(string hash) => Path.Combine(_root, $"{Guard(hash)}.png");

    /// <summary>
    /// A hash reaching the filesystem must be one we produced. Rejecting anything else keeps
    /// a value that has passed through a URL or a database from escaping the store directory.
    /// </summary>
    private static string Guard(string hash) =>
        ContentAddress.IsWellFormed(hash)
            ? hash
            : throw new ArgumentException($"'{hash}' is not a valid content address.", nameof(hash));

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort: a stray temp file is harmless next to a failed generation.
        }
    }
}

public sealed class ImageStoreOptions
{
    public const string SectionName = "ImageStore";

    public string Directory { get; set; } = Path.Combine("wwwroot", "img");

    /// <summary>URL prefix the store is served under.</summary>
    public string RelativePrefix { get; set; } = "img";
}
