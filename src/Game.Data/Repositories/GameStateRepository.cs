using System.Text.Json;
using Game.Core.Encounters;
using Game.Core.Saves;
using Game.Core.Scenes;
using Game.Core.Story;
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
    /// Records the player's choice of opening in one transaction: the clock starts at the opening's
    /// time, the opening and the main LI's home place are stored as flags, and the opening's places
    /// become known. Refused once an opening is chosen or a turn has been taken, so a save's start
    /// cannot be rewritten after the fact.
    /// </summary>
    public async Task StartOpeningAsync(
        SaveId saveId,
        string openingId,
        string homePlaceId,
        ClockState start,
        IReadOnlyList<string> reveal,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(openingId);
        ArgumentException.ThrowIfNullOrWhiteSpace(homePlaceId);
        ArgumentNullException.ThrowIfNull(reveal);

        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = """
                SELECT (SELECT COUNT(*) FROM flag WHERE save_id = $save AND key = 'opening')
                     + (SELECT COUNT(*) FROM visit WHERE save_id = $save);
                """;
            check.Parameters.AddWithValue("$save", saveId.ToString());

            if ((long)(await check.ExecuteScalarAsync(ct).ConfigureAwait(false))! > 0)
            {
                throw new InvalidOperationException($"Save '{saveId}' has already started; its opening cannot be chosen again.");
            }
        }

        await using (var clock = connection.CreateCommand())
        {
            clock.Transaction = transaction;
            clock.CommandText = """
                INSERT INTO game_clock (save_id, day, slot) VALUES ($save, $day, $slot)
                ON CONFLICT(save_id) DO UPDATE SET day = excluded.day, slot = excluded.slot;
                """;
            clock.Parameters.AddWithValue("$save", saveId.ToString());
            clock.Parameters.AddWithValue("$day", start.Day);
            clock.Parameters.AddWithValue("$slot", start.Slot.ToString());
            await clock.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await UpsertFlagsAsync(connection, transaction, saveId, new Dictionary<string, string>
        {
            ["opening"] = openingId,
            ["main_li.home_place"] = homePlaceId,
        }, ct).ConfigureAwait(false);

        foreach (var placeId in reveal)
        {
            await using var known = connection.CreateCommand();
            known.Transaction = transaction;
            known.CommandText = """
                UPDATE place SET known = 1, first_day = COALESCE(first_day, $day)
                WHERE save_id = $save AND id = $place;
                """;
            known.Parameters.AddWithValue("$save", saveId.ToString());
            known.Parameters.AddWithValue("$place", placeId);
            known.Parameters.AddWithValue("$day", start.Day);
            await known.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Answers an open choice in one transaction: closes it, records the answer and sets its flags.
    /// Refused unless <paramref name="encounterId"/>'s choice is the one open, so an answer cannot
    /// be given twice or to a choice that was never offered.
    /// </summary>
    public async Task ResolveChoiceAsync(
        SaveId saveId,
        string encounterId,
        string choiceId,
        IReadOnlyDictionary<string, string> flags,
        IReadOnlyDictionary<Guid, RelationshipState>? relationships = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encounterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(choiceId);
        ArgumentNullException.ThrowIfNull(flags);

        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var close = connection.CreateCommand())
        {
            close.Transaction = transaction;
            close.CommandText = """
                UPDATE flag SET value = 'false'
                WHERE save_id = $save AND key = $key AND value = $encounter;
                """;
            close.Parameters.AddWithValue("$save", saveId.ToString());
            close.Parameters.AddWithValue("$key", EncounterEvaluator.PendingChoiceKey);
            close.Parameters.AddWithValue("$encounter", encounterId);

            if (await close.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
            {
                throw new InvalidOperationException($"Save '{saveId}' has no open choice for '{encounterId}'.");
            }
        }

        var all = new Dictionary<string, string>(flags, StringComparer.Ordinal)
        {
            [EncounterEvaluator.ChoiceKey(encounterId)] = choiceId,
        };

        await UpsertFlagsAsync(connection, transaction, saveId, all, ct).ConfigureAwait(false);
        await WriteRelationshipsAsync(connection, transaction, saveId, relationships, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private static async Task WriteRelationshipsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SaveId saveId,
        IReadOnlyDictionary<Guid, RelationshipState>? relationships,
        CancellationToken ct)
    {
        foreach (var (characterId, relationship) in relationships ?? new Dictionary<Guid, RelationshipState>())
        {
            await StoryStateRepository.UpsertRelationshipAsync(connection, transaction, saveId, characterId, relationship, ct).ConfigureAwait(false);
        }
    }

    private static async Task UpsertFlagsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SaveId saveId,
        IReadOnlyDictionary<string, string> flags,
        CancellationToken ct)
    {
        foreach (var (key, value) in flags)
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
    }

    /// <summary>
    /// Applies a turn in one transaction: advances the clock, records the visit, sets flags and
    /// reveals places. The clock only advances from the slot the turn was planned at, so a turn
    /// planned twice from the same state (a double click, two tabs) is applied once and the second
    /// is refused, changing nothing. Relationship changes the turn caused commit with it.
    /// </summary>
    public async Task CommitTurnAsync(
        SaveId saveId,
        TurnOutcome outcome,
        IReadOnlyDictionary<Guid, RelationshipState>? relationships = null,
        CancellationToken ct = default)
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

        await UpsertFlagsAsync(connection, transaction, saveId, outcome.FlagsToSet, ct).ConfigureAwait(false);
        await WriteRelationshipsAsync(connection, transaction, saveId, relationships, ct).ConfigureAwait(false);

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
