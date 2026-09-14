using System.Text.Json;
using Game.Core.Encounters;
using Game.Core.Saves;
using Game.Core.Scenes;
using Game.Core.World;
using Microsoft.Data.Sqlite;

namespace Game.Data.Repositories;

/// <summary>A save's clock, flags and visits.</summary>
public sealed class GameStateRepository(Database database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The save's clock, starting it on day one if the save has none.</summary>
    public async Task<ClockState> GetOrStartClockAsync(SaveId saveId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        var start = ClockState.Start;
        command.CommandText = """
            INSERT INTO game_clock (save_id, day, slot) VALUES ($save, $day, $slot)
            ON CONFLICT(save_id) DO NOTHING;
            SELECT day, slot FROM game_clock WHERE save_id = $save;
            """;
        command.Parameters.AddWithValue("$save", saveId.ToString());
        command.Parameters.AddWithValue("$day", start.Day);
        command.Parameters.AddWithValue("$slot", start.Slot.ToString());

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? new ClockState(reader.GetInt32(0), Enum.Parse<TimeOfDay>(reader.GetString(1)))
            : throw new InvalidOperationException($"No save with id '{saveId}'.");
    }

    public async Task<IReadOnlyDictionary<string, string>> GetFlagsAsync(SaveId saveId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT key, value FROM flag WHERE save_id = $save;";
        command.Parameters.AddWithValue("$save", saveId.ToString());

        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            flags[reader.GetString(0)] = reader.GetString(1);
        }

        return flags;
    }

    /// <summary>Earlier visits to a place with no one else there.</summary>
    public async Task<int> CountAloneVisitsAsync(SaveId saveId, string placeId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT COUNT(*) FROM visit
            WHERE save_id = $save AND place_id = $place AND with_json = '[]';
            """;
        command.Parameters.AddWithValue("$save", saveId.ToString());
        command.Parameters.AddWithValue("$place", placeId);

        return (int)(long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    /// <summary>
    /// Applies a turn in one transaction: advances the clock, records the visit, sets flags and
    /// reveals places. The clock only advances from the slot the turn was planned at, so a turn
    /// planned twice from the same state (a double click, two tabs) is applied once and the second
    /// is refused, changing nothing.
    /// </summary>
    public async Task CommitTurnAsync(SaveId saveId, TurnOutcome outcome, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var clock = connection.CreateCommand())
        {
            clock.Transaction = transaction;
            clock.CommandText = """
                UPDATE game_clock SET day = $nextDay, slot = $nextSlot
                WHERE save_id = $save AND day = $day AND slot = $slot;
                """;
            clock.Parameters.AddWithValue("$save", saveId.ToString());
            clock.Parameters.AddWithValue("$day", outcome.VisitedAt.Day);
            clock.Parameters.AddWithValue("$slot", outcome.VisitedAt.Slot.ToString());
            clock.Parameters.AddWithValue("$nextDay", outcome.Next.Day);
            clock.Parameters.AddWithValue("$nextSlot", outcome.Next.Slot.ToString());

            if (await clock.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
            {
                throw new InvalidOperationException(
                    $"Save '{saveId}' is no longer at day {outcome.VisitedAt.Day}, {outcome.VisitedAt.Slot}; the turn was already taken.");
            }
        }

        await using (var visit = connection.CreateCommand())
        {
            visit.Transaction = transaction;
            visit.CommandText = """
                INSERT INTO visit (save_id, day, slot, place_id, with_json, encounter_id)
                VALUES ($save, $day, $slot, $place, $with, $encounter);
                """;
            visit.Parameters.AddWithValue("$save", saveId.ToString());
            visit.Parameters.AddWithValue("$day", outcome.VisitedAt.Day);
            visit.Parameters.AddWithValue("$slot", outcome.VisitedAt.Slot.ToString());
            visit.Parameters.AddWithValue("$place", outcome.PlaceId);
            visit.Parameters.AddWithValue("$with", JsonSerializer.Serialize(outcome.With, Json));
            visit.Parameters.AddWithValue("$encounter", outcome.EncounterId is null ? DBNull.Value : outcome.EncounterId);
            await visit.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var (key, value) in outcome.FlagsToSet)
        {
            await using var flag = connection.CreateCommand();
            flag.Transaction = transaction;
            flag.CommandText = """
                INSERT INTO flag (save_id, key, value) VALUES ($save, $key, $value)
                ON CONFLICT(save_id, key) DO UPDATE SET value = excluded.value;
                """;
            flag.Parameters.AddWithValue("$save", saveId.ToString());
            flag.Parameters.AddWithValue("$key", key);
            flag.Parameters.AddWithValue("$value", value);
            await flag.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var placeId in outcome.Reveals)
        {
            await using var reveal = connection.CreateCommand();
            reveal.Transaction = transaction;
            reveal.CommandText = """
                UPDATE place SET known = 1, first_day = COALESCE(first_day, $day)
                WHERE save_id = $save AND id = $place;
                """;
            reveal.Parameters.AddWithValue("$save", saveId.ToString());
            reveal.Parameters.AddWithValue("$place", placeId);
            reveal.Parameters.AddWithValue("$day", outcome.VisitedAt.Day);
            await reveal.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }
}
