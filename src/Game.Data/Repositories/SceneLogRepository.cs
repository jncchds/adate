using System.Text.Json;
using Game.Core.Saves;
using Game.Core.Scenes;
using Game.Core.Story;
using Game.Core.World;
using Microsoft.Data.Sqlite;

namespace Game.Data.Repositories;

/// <summary>
/// Every scene as the player saw it (migrations 010 and 011). A row is added when a turn is taken and
/// filled in as the picture, the person, the words and each exchange of the conversation arrive. Updates
/// address a scene by id, so a picture that finishes after the player has moved on never lands on the
/// next scene.
/// </summary>
public sealed class SceneLogRepository(Database database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string Columns = """
        id, day, slot, place_id, encounter_id, outcome_json, written, text, background_path, character_id,
        speaker, expression, sprite_path, exchanges_json, closed
        """;

    /// <summary>Records a new scene, closing whichever one was still open. Returns its id.</summary>
    public async Task<long> StartAsync(
        SaveId saveId,
        ClockState clock,
        string placeId,
        string? encounterId,
        string outcomeJson,
        string text,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(placeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcomeJson);
        ArgumentNullException.ThrowIfNull(text);

        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var close = connection.CreateCommand())
        {
            close.Transaction = transaction;
            close.CommandText = "UPDATE scene_log SET closed = 1 WHERE save_id = $save AND closed = 0;";
            close.Parameters.AddWithValue("$save", saveId.ToString());
            await close.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        long id;
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO scene_log (save_id, day, slot, place_id, encounter_id, outcome_json, text, created_utc)
                VALUES ($save, $day, $slot, $place, $encounter, $outcome, $text, $created)
                RETURNING id;
                """;
            insert.Parameters.AddWithValue("$save", saveId.ToString());
            insert.Parameters.AddWithValue("$day", clock.Day);
            insert.Parameters.AddWithValue("$slot", clock.Slot.ToString());
            insert.Parameters.AddWithValue("$place", placeId);
            insert.Parameters.AddWithValue("$encounter", (object?)encounterId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$outcome", outcomeJson);
            insert.Parameters.AddWithValue("$text", text);
            insert.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
            id = (long)(await insert.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return id;
    }

    /// <summary>The scene the player is in: the newest one not closed.</summary>
    public async Task<StoredScene?> GetOpenAsync(SaveId saveId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = $"SELECT {Columns} FROM scene_log WHERE save_id = $save AND closed = 0 ORDER BY id DESC LIMIT 1;";
        command.Parameters.AddWithValue("$save", saveId.ToString());

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    /// <summary>Where the player's latest scene happened, open or not; null before the first turn.</summary>
    public async Task<string?> GetLastPlaceIdAsync(SaveId saveId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT place_id FROM scene_log WHERE save_id = $save ORDER BY id DESC LIMIT 1;";
        command.Parameters.AddWithValue("$save", saveId.ToString());

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    /// <summary>Every scene of a save, oldest first.</summary>
    public async Task<IReadOnlyList<StoredScene>> ListAsync(SaveId saveId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = $"SELECT {Columns} FROM scene_log WHERE save_id = $save ORDER BY id;";
        command.Parameters.AddWithValue("$save", saveId.ToString());

        var scenes = new List<StoredScene>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            scenes.Add(Map(reader));
        }

        return scenes;
    }

    public Task SetBackgroundAsync(long sceneId, string path, CancellationToken ct = default) =>
        UpdateAsync(sceneId, "background_path = $path", ct, ("$path", path));

    /// <summary>The person as shown now: who, at which expression, in which picture.</summary>
    public Task SetPersonAsync(long sceneId, Guid? characterId, string? speaker, string? expression, string? spritePath, CancellationToken ct = default) =>
        UpdateAsync(
            sceneId,
            "character_id = $character, speaker = $speaker, expression = $expression, sprite_path = $sprite",
            ct,
            ("$character", characterId?.ToString()),
            ("$speaker", speaker),
            ("$expression", expression),
            ("$sprite", spritePath));

    /// <summary>The finished words, and the expression they end on when they say one.</summary>
    public Task SetWrittenAsync(long sceneId, string text, string? expression, CancellationToken ct = default) =>
        UpdateAsync(sceneId, "written = 1, text = $text, expression = COALESCE($expression, expression)", ct, ("$text", text), ("$expression", expression));

    /// <summary>Adds one exchange of the conversation to the end, in a single statement.</summary>
    public Task AddExchangeAsync(long sceneId, SceneExchange exchange, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(exchange);

        return UpdateAsync(
            sceneId,
            "exchanges_json = json_insert(COALESCE(exchanges_json, '[]'), '$[#]', json($exchange))",
            ct,
            ("$exchange", JsonSerializer.Serialize(exchange, Json)));
    }

    /// <summary>The player has moved on from the scene they were in.</summary>
    public async Task CloseAsync(SaveId saveId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = "UPDATE scene_log SET closed = 1 WHERE save_id = $save AND closed = 0;";
        command.Parameters.AddWithValue("$save", saveId.ToString());
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task UpdateAsync(long sceneId, string assignments, CancellationToken ct, params (string Name, string? Value)[] values)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = $"UPDATE scene_log SET {assignments} WHERE id = $id;";
        command.Parameters.AddWithValue("$id", sceneId);
        foreach (var (name, value) in values)
        {
            command.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static StoredScene Map(SqliteDataReader reader)
    {
        string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);

        return new StoredScene(
            reader.GetInt64(0),
            new ClockState(reader.GetInt32(1), Enum.Parse<TimeOfDay>(reader.GetString(2))),
            reader.GetString(3),
            Text(4),
            reader.GetString(5),
            reader.GetInt64(6) == 1,
            reader.GetString(7),
            Text(8),
            Text(9) is { } character ? Guid.Parse(character) : null,
            Text(10),
            Text(11),
            Text(12),
            Text(13) is { } exchanges ? JsonSerializer.Deserialize<List<SceneExchange>>(exchanges, Json) ?? [] : [],
            reader.GetInt64(14) == 1);
    }
}
