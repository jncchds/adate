using Game.Core.Saves;
using Game.Core.Story;
using Microsoft.Data.Sqlite;

namespace Game.Data.Repositories;

/// <summary>Happenings the model proposed for a kind of place and C# accepted (migration 021).</summary>
public sealed class HappeningIdeaRepository(Database database)
{
    /// <summary>Keeps the ideas that are new to this save and fit under the place type's pool; returns how many were kept.</summary>
    public async Task<int> AddAsync(SaveId saveId, string placeTypeId, IEnumerable<string> texts, int day, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(placeTypeId);
        ArgumentNullException.ThrowIfNull(texts);

        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        var unused = await CountUnusedAsync(connection, saveId, placeTypeId, ct).ConfigureAwait(false);
        var added = 0;

        foreach (var text in texts)
        {
            if (unused + added >= HappeningIdeas.PoolPerPlaceType)
            {
                break;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO happening_idea (save_id, place_type_id, text, proposed_day) VALUES ($save, $type, $text, $day);";
            command.Parameters.AddWithValue("$save", saveId.ToString());
            command.Parameters.AddWithValue("$type", placeTypeId);
            command.Parameters.AddWithValue("$text", text);
            command.Parameters.AddWithValue("$day", day);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            added++;
        }

        return added;
    }

    /// <summary>Every idea of a save, kept or used, to stop a new one repeating an old one.</summary>
    public async Task<IReadOnlyList<string>> TextsAsync(SaveId saveId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT text FROM happening_idea WHERE save_id = $save;";
        command.Parameters.AddWithValue("$save", saveId.ToString());

        var texts = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            texts.Add(reader.GetString(0));
        }

        return texts;
    }

    /// <summary>
    /// The idea for the scene at <paramref name="clock"/> in this kind of place: the one that scene already took, else
    /// the oldest unused one, which is then marked as taken by it. Null when there is none.
    /// </summary>
    public async Task<string?> TakeAsync(SaveId saveId, string placeTypeId, string clock, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);

        async Task<(long Id, string Text)?> FindAsync(string where)
        {
            await using var find = connection.CreateCommand();
            find.CommandText = $"SELECT id, text FROM happening_idea WHERE save_id = $save AND place_type_id = $type AND {where} ORDER BY id LIMIT 1;";
            find.Parameters.AddWithValue("$save", saveId.ToString());
            find.Parameters.AddWithValue("$type", placeTypeId);
            find.Parameters.AddWithValue("$clock", clock);
            await using var reader = await find.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false) ? (reader.GetInt64(0), reader.GetString(1)) : null;
        }

        if (await FindAsync("used_at = $clock").ConfigureAwait(false) is { } again)
        {
            return again.Text;
        }

        if (await FindAsync("used_at IS NULL").ConfigureAwait(false) is not { } next)
        {
            return null;
        }

        await using var mark = connection.CreateCommand();
        mark.CommandText = "UPDATE happening_idea SET used_at = $clock WHERE id = $id;";
        mark.Parameters.AddWithValue("$clock", clock);
        mark.Parameters.AddWithValue("$id", next.Id);
        await mark.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return next.Text;
    }

    private static async Task<int> CountUnusedAsync(SqliteConnection connection, SaveId saveId, string placeTypeId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM happening_idea WHERE save_id = $save AND place_type_id = $type AND used_at IS NULL;";
        command.Parameters.AddWithValue("$save", saveId.ToString());
        command.Parameters.AddWithValue("$type", placeTypeId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }
}
