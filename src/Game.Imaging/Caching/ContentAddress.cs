using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Game.Imaging.Caching;

/// <summary>
/// HANDOFF 1.7: filename is sha256 of every generation parameter, which collapses cache
/// lookup and HTTP caching into one mechanism. Files are served immutable because the
/// name can only describe one possible set of inputs.
/// </summary>
public static class ContentAddress
{
    /// <summary>
    /// Field separator (ASCII unit separator). Chosen because it cannot occur in a prompt,
    /// a hash or a number, so no combination of field values can be made to collide by
    /// shifting a field boundary.
    /// </summary>
    private const char Separator = (char)0x1F;

    /// <summary>
    /// Computes the address of a request. The field order below is part of the on-disk
    /// contract: reordering or inserting a field invalidates every cached image in
    /// existence. Append only, and only alongside a bump of the version tag.
    /// </summary>
    public static string For(ImageRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);

        var sb = new StringBuilder();
        Append(sb, "v1");
        Append(sb, req.WorkflowId);
        Append(sb, req.PackFingerprint);
        Append(sb, req.Positive);
        Append(sb, req.Negative);
        Append(sb, req.Seed.ToString(CultureInfo.InvariantCulture));
        Append(sb, req.Width.ToString(CultureInfo.InvariantCulture));
        Append(sb, req.Height.ToString(CultureInfo.InvariantCulture));
        Append(sb, req.AnchorImageHash ?? string.Empty);
        Append(sb, req.AnchorWeight?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty);
        Append(sb, ((int)req.Ceiling).ToString(CultureInfo.InvariantCulture));

        return Hash(sb.ToString());
    }

    /// <summary>Content hash of raw bytes. Used to address uploaded anchors and outputs.</summary>
    public static string OfBytes(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// Fingerprint of a style pack manifest, fed into <see cref="ImageRequest.PackFingerprint"/>.
    /// Hashes the manifest's raw JSON so any edit to checkpoint, LoRAs or sampler settings
    /// invalidates art generated under the previous version.
    /// </summary>
    public static string OfManifest(string manifestJson) => Hash(manifestJson);

    /// <summary>Sanity guard: a hash used as a filename must be exactly what we produced.</summary>
    public static bool IsWellFormed(string hash) =>
        hash.Length == 64 && hash.All(static c => (c is >= '0' and <= '9') || (c is >= 'a' and <= 'f'));

    private static void Append(StringBuilder sb, string value)
    {
        sb.Append(value);
        sb.Append(Separator);
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
