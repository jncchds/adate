using Game.Core.Saves;
using Game.Core.Story;
using Microsoft.Data.Sqlite;

namespace Game.Data.Repositories;

/// <summary>Loose ends scenes leave open (migration 012), and whether a save's first ones were read from its scene log.</summary>
public sealed class ThreadRepository(Database database)
{
    public async Task AddAsync(SaveId saveId, string? characterId, string text, int day, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = "INSERT INTO story_thread (save_id, character_id, text, opened_day) VALUES ($save, $character, $text, $day);";
        command.Parameters.AddWithValue("$save", saveId.ToString());
        command.Parameters.AddWithValue("$character", (object?)characterId ?? DBNull.Value);
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$day", day);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Closes the given loose ends of this save; ids of other saves or already closed ones are ignored.</summary>
    public async Task CloseAsync(SaveId saveId, IEnumerable<long> ids, int day, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        foreach (var id in ids.Distinct())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE story_thread SET closed_day = $day WHERE id = $id AND save_id = $save AND closed_day IS NULL;";
            command.Parameters.AddWithValue("$day", day);
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$save", saveId.ToString());
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Every loose end of a save, oldest first; open ones only unless <paramref name="openOnly"/> is false.</summary>
    public async Task<IReadOnlyList<StoryThread>> ListAsync(SaveId saveId, bool openOnly = true, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT id, character_id, text, opened_day, closed_day FROM story_thread WHERE save_id = $save"
            + (openOnly ? " AND closed_day IS NULL" : "") + " ORDER BY id;";
        command.Parameters.AddWithValue("$save", saveId.ToString());

        var threads = new List<StoryThread>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            threads.Add(new StoryThread(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4)));
        }

        return threads;
    }

    /// <summary>Records that a save's first loose ends were read; true when this call recorded it, false when it already was.</summary>
    public async Task<bool> MarkSeededAsync(SaveId saveId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = "INSERT OR IGNORE INTO thread_seed (save_id) VALUES ($save);";
        command.Parameters.AddWithValue("$save", saveId.ToString());
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    public async Task<bool> IsSeededAsync(SaveId saveId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT 1 FROM thread_seed WHERE save_id = $save;";
        command.Parameters.AddWithValue("$save", saveId.ToString());
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }
}
