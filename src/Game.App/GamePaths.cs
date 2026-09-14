namespace Game.App;

/// <summary>Where one head keeps what the game ships and what the player owns.</summary>
/// <param name="ContentRoot">Style packs, content and workflow graphs, read only.</param>
/// <param name="DataRoot">The database, the image cache and the player's server addresses.</param>
public sealed record GamePaths(string ContentRoot, string DataRoot)
{
    /// <summary>Overrides the data folder on desktop, so a second copy can keep separate saves.</summary>
    public const string DataVariable = "ADATE_DATA";

    /// <summary>The player's server addresses, layered over the shipped defaults.</summary>
    public string SettingsFile => Path.Combine(DataRoot, "settings.json");

    public string DatabaseFile => Path.Combine(DataRoot, "adate.db");

    public string ImageDirectory => Path.Combine(DataRoot, "img");

    /// <summary>Content next to the executable; data in the user's local application data.</summary>
    public static GamePaths ForDesktop() => new(
        AppContext.BaseDirectory,
        Environment.GetEnvironmentVariable(DataVariable) is { Length: > 0 } data
            ? data
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "adate"));
}
