using Game.Core;
using Game.Core.Saves;
using Game.Core.Scenes;

namespace Game.Data.Repositories;

/// <summary>
/// Index over generated art. The image bytes live in the content-addressed store; these
/// rows are what let the game find a hash again from game-domain terms — this character,
/// this expression, this ceiling — without recomputing a generation request.
/// </summary>
public sealed class ImageCacheRepository(Database database)
{
    // ------------------------------------------------------------------ sprites

    public async Task RecordSpriteAsync(
        string hash,
        SaveId saveId,
        Guid characterId,
        string outfit,
        string pose,
        string expression,
        Ceiling ceiling,
        string path,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        // The hash already encodes every generation parameter, so re-recording the same
        // hash is a no-op rather than a conflict.
        command.CommandText = """
            INSERT INTO sprite_cache
                (hash, save_id, character_id, outfit, pose, expression, ceiling, path, created_utc)
            VALUES ($hash, $save, $character, $outfit, $pose, $expression, $ceiling, $path, $created)
            ON CONFLICT(hash) DO NOTHING;
            """;

        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$save", saveId.ToString());
        command.Parameters.AddWithValue("$character", characterId.ToString());
        command.Parameters.AddWithValue("$outfit", outfit);
        command.Parameters.AddWithValue("$pose", pose);
        command.Parameters.AddWithValue("$expression", expression);
        command.Parameters.AddWithValue("$ceiling", (int)ceiling);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// HANDOFF 1.8: ceiling is part of the lookup key, not a filter applied to the result.
    /// A sprite generated at one ceiling must never be served into a session at another.
    /// </summary>
    public async Task<string?> FindSpritePathAsync(
        SaveId saveId,
        Guid characterId,
        string outfit,
        string pose,
        string expression,
        Ceiling ceiling,
        CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT path FROM sprite_cache
            WHERE save_id = $save
              AND character_id = $character
              AND outfit = $outfit
              AND pose = $pose
              AND expression = $expression
              AND ceiling = $ceiling
            LIMIT 1;
            """;

        command.Parameters.AddWithValue("$save", saveId.ToString());
        command.Parameters.AddWithValue("$character", characterId.ToString());
        command.Parameters.AddWithValue("$outfit", outfit);
        command.Parameters.AddWithValue("$pose", pose);
        command.Parameters.AddWithValue("$expression", expression);
        command.Parameters.AddWithValue("$ceiling", (int)ceiling);

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    /// <summary>
    /// Every expression already generated for one look. The scene viewer preloads these so
    /// an expression change is a CSS crossfade between cached images and no server work.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> ListExpressionsAsync(
        SaveId saveId,
        Guid characterId,
        string outfit,
        string pose,
        Ceiling ceiling,
        CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT expression, path FROM sprite_cache
            WHERE save_id = $save
              AND character_id = $character
              AND outfit = $outfit
              AND pose = $pose
              AND ceiling = $ceiling
            ORDER BY expression;
            """;

        command.Parameters.AddWithValue("$save", saveId.ToString());
        command.Parameters.AddWithValue("$character", characterId.ToString());
        command.Parameters.AddWithValue("$outfit", outfit);
        command.Parameters.AddWithValue("$pose", pose);
        command.Parameters.AddWithValue("$ceiling", (int)ceiling);

        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            found[reader.GetString(0)] = reader.GetString(1);
        }

        return found;
    }

    // -------------------------------------------------------------- backgrounds

    public async Task RecordBackgroundAsync(
        string hash,
        SaveId saveId,
        string locationId,
        TimeOfDay time,
        string path,
        string weather = "clear",
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        ArgumentException.ThrowIfNullOrWhiteSpace(locationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(weather);

        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO background_cache (hash, save_id, location_id, time_of_day, weather, path, created_utc)
            VALUES ($hash, $save, $location, $time, $weather, $path, $created)
            ON CONFLICT(hash) DO NOTHING;
            """;

        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$save", saveId.ToString());
        command.Parameters.AddWithValue("$location", locationId);
        command.Parameters.AddWithValue("$time", time.ToString());
        command.Parameters.AddWithValue("$weather", weather);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<string?> FindBackgroundPathAsync(
        SaveId saveId,
        string locationId,
        TimeOfDay time,
        string weather = "clear",
        CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT path FROM background_cache
            WHERE save_id = $save AND location_id = $location AND time_of_day = $time AND weather = $weather
            LIMIT 1;
            """;

        command.Parameters.AddWithValue("$save", saveId.ToString());
        command.Parameters.AddWithValue("$location", locationId);
        command.Parameters.AddWithValue("$time", time.ToString());
        command.Parameters.AddWithValue("$weather", weather);

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }
}
