using Android.Content.Res;

namespace Game.Android;

/// <summary>
/// Copies the shipped content out of the APK into app storage, where the game reads files by path
/// like every other head. It is under 200 KB, so it is copied fresh on every start: an update can
/// never leave stale or deleted content behind.
/// </summary>
internal static class ContentAssets
{
    private static readonly string[] Roots = ["content", "stylepacks", "workflows"];

    /// <returns>The folder the content was copied to.</returns>
    public static string Extract(AssetManager assets, string dataRoot)
    {
        var target = Path.Combine(dataRoot, "shipped");
        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }

        foreach (var root in Roots)
        {
            Copy(assets, root, Path.Combine(target, root));
        }

        return target;
    }

    private static void Copy(AssetManager assets, string assetPath, string destination)
    {
        var children = assets.List(assetPath) ?? [];

        // An asset path with no children is a file; the asset manager lists folders only.
        if (children.Length == 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = assets.Open(assetPath);
            using var output = File.Create(destination);
            input.CopyTo(output);
            return;
        }

        foreach (var child in children)
        {
            Copy(assets, $"{assetPath}/{child}", Path.Combine(destination, child));
        }
    }
}
