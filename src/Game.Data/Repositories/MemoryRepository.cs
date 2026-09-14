using System.Runtime.InteropServices;
using System.Text.Json;
using Game.Core.Saves;
using Game.Core.Story;
using Microsoft.Data.Sqlite;

namespace Game.Data.Repositories;

/// <summary>A save's memories: scene summaries and the day and week summaries they compact into.</summary>
public sealed class MemoryRepository(Database database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Stores a memory and returns its id. The entry's own id and compaction are ignored.</summary>
    public async Task<long> AddAsync(SaveId saveId, MemoryEntry memory, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        return await InsertAsync(connection, null, saveId, memory, ct).ConfigureAwait(false);
    }

    /// <summary>Every memory of the save, compacted ones included, in the order stored.</summary>
    public async Task<IReadOnlyList<MemoryEntry>> ListAsync(SaveId saveId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT id, scope, day, summary, people_json, tags_json, embedding, compacted_into
            FROM memory WHERE save_id = $save ORDER BY id;
            """;
        command.Parameters.AddWithValue("$save", saveId.ToString());

        var memories = new List<MemoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            memories.Add(new MemoryEntry(
                reader.GetInt64(0),
                Enum.Parse<MemoryScope>(reader.GetString(1)),
                reader.GetInt32(2),
                reader.GetString(3),
                JsonSerializer.Deserialize<List<string>>(reader.GetString(4), Json)!,
                JsonSerializer.Deserialize<List<string>>(reader.GetString(5), Json)!,
                reader.IsDBNull(6) ? null : MemoryMarshal.Cast<byte, float>((byte[])reader.GetValue(6)).ToArray(),
                reader.IsDBNull(7) ? null : reader.GetInt64(7)));
        }

        return memories;
    }

    /// <summary>
    /// Stores <paramref name="summary"/> and folds <paramref name="memberIds"/> into it, in one
    /// transaction. Refused, with nothing written, if any member is already compacted or is tagged
    /// first or conflict.
    /// </summary>
    public async Task<long> CompactAsync(SaveId saveId, MemoryEntry summary, IReadOnlyList<long> memberIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(memberIds);

        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var id = await InsertAsync(connection, transaction, saveId, summary, ct).ConfigureAwait(false);

        foreach (var member in memberIds)
        {
            await using var fold = connection.CreateCommand();
            fold.Transaction = transaction;
            fold.CommandText = "UPDATE memory SET compacted_into = $into WHERE id = $id AND save_id = $save;";
            fold.Parameters.AddWithValue("$into", id);
            fold.Parameters.AddWithValue("$id", member);
            fold.Parameters.AddWithValue("$save", saveId.ToString());

            if (await fold.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
            {
                throw new InvalidOperationException($"Save '{saveId}' has no memory {member}.");
            }
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return id;
    }

    private static async Task<long> InsertAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        SaveId saveId,
        MemoryEntry memory,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentException.ThrowIfNullOrWhiteSpace(memory.Summary);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO memory (save_id, scope, day, summary, people_json, tags_json, embedding, created_utc)
            VALUES ($save, $scope, $day, $summary, $people, $tags, $embedding, $created);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$save", saveId.ToString());
        command.Parameters.AddWithValue("$scope", memory.Scope.ToString());
        command.Parameters.AddWithValue("$day", memory.Day);
        command.Parameters.AddWithValue("$summary", memory.Summary);
        command.Parameters.AddWithValue("$people", JsonSerializer.Serialize(memory.People, Json));
        command.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(memory.Tags, Json));
        command.Parameters.Add("$embedding", SqliteType.Blob).Value =
            memory.Embedding is null ? DBNull.Value : MemoryMarshal.AsBytes(memory.Embedding.AsSpan()).ToArray();
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));

        return (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }
}
