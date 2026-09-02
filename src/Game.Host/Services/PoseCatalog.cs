using Game.Imaging;
using Game.Imaging.Caching;

namespace Game.Host.Services;

/// <summary>
/// Authored OpenPose skeletons, one per pose slot, checked into <c>content/poses</c>.
/// </summary>
/// <remarks>
/// <para>
/// A skeleton is content, not a generated artefact: it is extracted once, judged once, and
/// then every expression in a set is conditioned on the same file. That is what holds the
/// body still enough for the scene viewer to crossfade between expressions instead of
/// cutting between two differently-posed people.
/// </para>
/// <para>
/// Skeletons are copied into the image store on first use because that is where
/// <see cref="ComfyImageProvider"/> reads pose inputs from, and its content hash is what a
/// request refers to. Copying is idempotent: the hash is of the file's own bytes, so a
/// skeleton that is already there is left alone.
/// </para>
/// </remarks>
public sealed class PoseCatalog(IImageStore store, string directory)
{
    private readonly Dictionary<string, string> _hashes = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// The store hash of the skeleton for <paramref name="pose"/>, seeding the store if the
    /// file has not been used yet.
    /// </summary>
    public async Task<string> HashForAsync(string pose, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pose);

        if (_hashes.TryGetValue(pose, out var cached))
        {
            return cached;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_hashes.TryGetValue(pose, out cached))
            {
                return cached;
            }

            var path = Path.Combine(directory, $"{pose}.png");
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"No pose skeleton for '{pose}'. Expected an OpenPose render at '{path}'. " +
                    "Skeletons are authored content and are not generated on demand.",
                    path);
            }

            var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            var hash = ContentAddress.OfBytes(bytes);

            if (!store.Exists(hash))
            {
                await store.SaveAsync(hash, bytes, ct).ConfigureAwait(false);
            }

            _hashes[pose] = hash;
            return hash;
        }
        finally
        {
            _gate.Release();
        }
    }
}
