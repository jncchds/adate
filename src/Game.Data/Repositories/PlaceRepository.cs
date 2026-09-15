using System.Text.Json;
using Game.Core.Places;
using Game.Core.Saves;
using Microsoft.Data.Sqlite;

namespace Game.Data.Repositories;

public sealed class PlaceRepository(Database database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Inserts places a save does not already have. Idempotent, so a setting's authored places can
    /// be ensured on every visit without ever overwriting a place the story has since changed.
    /// </summary>
    public async Task AddAsync(IEnumerable<PlaceRecord> places, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(places);

        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        foreach (var place in places)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO place (save_id, id, type_id, name, details_json, seed, origin, known, first_day, look, created_utc)
                VALUES ($save, $id, $type, $name, $details, $seed, $origin, $known, $firstDay, $look, $created)
                ON CONFLICT(save_id, id) DO NOTHING;
                """;

            command.Parameters.AddWithValue("$save", place.SaveId.ToString());
            command.Parameters.AddWithValue("$id", place.Id);
            command.Parameters.AddWithValue("$type", place.TypeId);
            command.Parameters.AddWithValue("$name", place.Name);
            command.Parameters.AddWithValue("$details", JsonSerializer.Serialize(place.Details, Json));
            command.Parameters.AddWithValue("$seed", place.Seed);
            command.Parameters.AddWithValue("$origin", place.Origin is PlaceOrigin.Authored ? "authored" : "story");
            command.Parameters.AddWithValue("$known", place.Known ? 1 : 0);
            command.Parameters.AddWithValue("$firstDay", place.FirstDay is { } day ? day : DBNull.Value);
            command.Parameters.AddWithValue("$look", (object?)place.Look ?? DBNull.Value);
            command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));

            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>A save's places in the order they were added: setting order, then story order.</summary>
    public async Task<IReadOnlyList<PlaceRecord>> ListAsync(SaveId saveId, bool knownOnly = false, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT save_id, id, type_id, name, details_json, seed, origin, known, first_day, look
            FROM place
            WHERE save_id = $save AND ($knownOnly = 0 OR known = 1)
            ORDER BY rowid;
            """;
        command.Parameters.AddWithValue("$save", saveId.ToString());
        command.Parameters.AddWithValue("$knownOnly", knownOnly ? 1 : 0);

        var places = new List<PlaceRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            places.Add(Map(reader));
        }

        return places;
    }

    public async Task<PlaceRecord?> GetAsync(SaveId saveId, string placeId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT save_id, id, type_id, name, details_json, seed, origin, known, first_day, look
            FROM place
            WHERE save_id = $save AND id = $id;
            """;
        command.Parameters.AddWithValue("$save", saveId.ToString());
        command.Parameters.AddWithValue("$id", placeId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    /// <summary>Makes a place known from <paramref name="day"/>. A place already known keeps its first day.</summary>
    public async Task MarkKnownAsync(SaveId saveId, string placeId, int day, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            UPDATE place SET known = 1, first_day = COALESCE(first_day, $day)
            WHERE save_id = $save AND id = $id;
            """;
        command.Parameters.AddWithValue("$save", saveId.ToString());
        command.Parameters.AddWithValue("$id", placeId);
        command.Parameters.AddWithValue("$day", day);

        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
        {
            throw new InvalidOperationException($"Save '{saveId}' has no place '{placeId}'.");
        }
    }

    private static PlaceRecord Map(SqliteDataReader reader) => new(
        SaveId.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        JsonSerializer.Deserialize<List<string>>(reader.GetString(4), Json) ?? [],
        reader.GetInt64(5),
        reader.GetString(6) == "authored" ? PlaceOrigin.Authored : PlaceOrigin.Story,
        reader.GetInt64(7) == 1,
        reader.IsDBNull(8) ? null : reader.GetInt32(8),
        reader.IsDBNull(9) ? null : reader.GetString(9));
}
