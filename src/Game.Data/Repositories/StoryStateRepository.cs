using System.Text.Json;
using System.Text.Json.Serialization;
using Game.Core.Saves;
using Game.Core.Scenes;
using Game.Core.Story;
using Game.Core.World;
using Microsoft.Data.Sqlite;

namespace Game.Data.Repositories;

/// <summary>A save's facts, knowledge, relationships, profiles, schedules, promises and turn log.</summary>
public sealed class StoryStateRepository(Database database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    // -------------------------------------------------------------------- facts

    /// <summary>
    /// Checks a fact against the ones held and stores it if the ledger allows, in one transaction.
    /// A rejected fact writes nothing. A duplicate stores nothing new, but its knowers still learn it.
    /// </summary>
    public async Task<FactCheck> AddFactAsync(
        SaveId saveId,
        Fact fact,
        PredicateDefinition? predicate,
        IReadOnlyCollection<string> knowers,
        string? explainedBy = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentNullException.ThrowIfNull(knowers);

        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var current = await ReadFactsAsync(connection, transaction, saveId, fact.Subject, fact.Predicate, ct).ConfigureAwait(false);
        var check = FactLedger.Check(current, fact, predicate, explainedBy);

        if (check.Verdict is FactVerdict.Rejected)
        {
            return check;
        }

        long id;
        if (check.Verdict is FactVerdict.Duplicate)
        {
            id = check.ExistingId!.Value;
        }
        else
        {
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO fact (save_id, subject, predicate, object, level, source, day, explained_by)
                    VALUES ($save, $subject, $predicate, $object, $level, $source, $day, $explained);
                    SELECT last_insert_rowid();
                    """;
                insert.Parameters.AddWithValue("$save", saveId.ToString());
                insert.Parameters.AddWithValue("$subject", fact.Subject);
                insert.Parameters.AddWithValue("$predicate", fact.Predicate);
                insert.Parameters.AddWithValue("$object", fact.Object);
                insert.Parameters.AddWithValue("$level", fact.Level.ToString());
                insert.Parameters.AddWithValue("$source", fact.Source);
                insert.Parameters.AddWithValue("$day", fact.Day);
                insert.Parameters.AddWithValue("$explained", (object?)explainedBy ?? DBNull.Value);
                id = (long)(await insert.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
            }

            foreach (var old in check.Supersedes)
            {
                await using var supersede = connection.CreateCommand();
                supersede.Transaction = transaction;
                supersede.CommandText = """
                    UPDATE fact SET superseded_by = $id
                    WHERE id = $old AND save_id = $save AND superseded_by IS NULL;
                    """;
                supersede.Parameters.AddWithValue("$id", id);
                supersede.Parameters.AddWithValue("$old", old);
                supersede.Parameters.AddWithValue("$save", saveId.ToString());
                await supersede.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        foreach (var knower in knowers)
        {
            await GrantAsync(connection, transaction, id, knower, fact.Day, ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return check with { ExistingId = id };
    }

    /// <summary>The save's current facts, each with who knows it, in the order they were stored.</summary>
    public async Task<IReadOnlyList<KnownFact>> GetFactsAsync(SaveId saveId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        return await ReadFactsAsync(connection, null, saveId, null, null, ct).ConfigureAwait(false);
    }

    /// <summary>Records that <paramref name="knower"/> learned a fact. Returns false if they already knew it.</summary>
    public async Task<bool> LearnAsync(SaveId saveId, long factId, string knower, int day, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(knower);

        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT COUNT(*) FROM fact WHERE id = $fact AND save_id = $save;
            """;
        command.Parameters.AddWithValue("$fact", factId);
        command.Parameters.AddWithValue("$save", saveId.ToString());

        if ((long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))! == 0)
        {
            throw new InvalidOperationException($"Save '{saveId}' has no fact {factId}.");
        }

        return await GrantAsync(connection, null, factId, knower, day, ct).ConfigureAwait(false);
    }

    private static async Task<bool> GrantAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long factId,
        string knower,
        int day,
        CancellationToken ct)
    {
        await using var grant = connection.CreateCommand();
        grant.Transaction = transaction;
        grant.CommandText = """
            INSERT OR IGNORE INTO fact_knowledge (fact_id, knower, day) VALUES ($fact, $knower, $day);
            """;
        grant.Parameters.AddWithValue("$fact", factId);
        grant.Parameters.AddWithValue("$knower", knower);
        grant.Parameters.AddWithValue("$day", day);
        return await grant.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    private static async Task<IReadOnlyList<KnownFact>> ReadFactsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        SaveId saveId,
        string? subject,
        string? predicate,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT f.id, f.subject, f.predicate, f.object, f.level, f.source, f.day, k.knower
            FROM fact f
            LEFT JOIN fact_knowledge k ON k.fact_id = f.id
            WHERE f.save_id = $save
              AND f.superseded_by IS NULL
              AND ($subject IS NULL OR f.subject = $subject)
              AND ($predicate IS NULL OR f.predicate = $predicate)
            ORDER BY f.id;
            """;
        command.Parameters.AddWithValue("$save", saveId.ToString());
        command.Parameters.AddWithValue("$subject", (object?)subject ?? DBNull.Value);
        command.Parameters.AddWithValue("$predicate", (object?)predicate ?? DBNull.Value);

        var order = new List<long>();
        var facts = new Dictionary<long, (Fact Fact, HashSet<string> Knowers)>();

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var id = reader.GetInt64(0);
            if (!facts.TryGetValue(id, out var entry))
            {
                entry = (new Fact(
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    Enum.Parse<FactLevel>(reader.GetString(4)),
                    reader.GetString(5),
                    reader.GetInt32(6)), new HashSet<string>(StringComparer.Ordinal));
                facts[id] = entry;
                order.Add(id);
            }

            if (!reader.IsDBNull(7))
            {
                entry.Knowers.Add(reader.GetString(7));
            }
        }

        return [.. order.Select(id => new KnownFact(id, facts[id].Fact, facts[id].Knowers))];
    }

    // -------------------------------------------------------------------- relationships

    /// <summary>A character's relationship with the player; <see cref="RelationshipState.Start"/> before any.</summary>
    public async Task<RelationshipState> GetRelationshipAsync(SaveId saveId, Guid characterId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT affection, trust, attraction, suspicion, stage, gain_day, day_gain, dealbreaker
            FROM rel_state WHERE save_id = $save AND character_id = $character;
            """;
        command.Parameters.AddWithValue("$save", saveId.ToString());
        command.Parameters.AddWithValue("$character", characterId.ToString());

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? new RelationshipState(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                Enum.Parse<RelationshipStage>(reader.GetString(4)),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt64(7) == 1)
            : RelationshipState.Start;
    }

    public async Task SaveRelationshipAsync(SaveId saveId, Guid characterId, RelationshipState state, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await UpsertRelationshipAsync(connection, null, saveId, characterId, state, ct).ConfigureAwait(false);
    }

    /// <summary>Shared with the turn and choice transactions, so a relationship change commits with them or not at all.</summary>
    internal static async Task UpsertRelationshipAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        SaveId saveId,
        Guid characterId,
        RelationshipState state,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO rel_state (save_id, character_id, affection, trust, attraction, suspicion, stage, gain_day, day_gain, dealbreaker)
            VALUES ($save, $character, $affection, $trust, $attraction, $suspicion, $stage, $gainDay, $dayGain, $dealbreaker)
            ON CONFLICT(save_id, character_id) DO UPDATE SET
                affection = excluded.affection,
                trust = excluded.trust,
                attraction = excluded.attraction,
                suspicion = excluded.suspicion,
                stage = excluded.stage,
                gain_day = excluded.gain_day,
                day_gain = excluded.day_gain,
                dealbreaker = excluded.dealbreaker;
            """;
        command.Parameters.AddWithValue("$save", saveId.ToString());
        command.Parameters.AddWithValue("$character", characterId.ToString());
        command.Parameters.AddWithValue("$affection", state.Affection);
        command.Parameters.AddWithValue("$trust", state.Trust);
        command.Parameters.AddWithValue("$attraction", state.Attraction);
        command.Parameters.AddWithValue("$suspicion", state.Suspicion);
        command.Parameters.AddWithValue("$stage", state.Stage.ToString());
        command.Parameters.AddWithValue("$gainDay", state.GainDay);
        command.Parameters.AddWithValue("$dayGain", state.DayGain);
        command.Parameters.AddWithValue("$dealbreaker", state.Dealbreaker ? 1 : 0);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------- profiles

    public async Task<StoryProfile?> GetProfileAsync(Guid characterId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT story_json FROM character WHERE id = $id;";
        command.Parameters.AddWithValue("$id", characterId.ToString());

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is string json
            ? JsonSerializer.Deserialize<StoryProfile>(json, Json)
            : null;
    }

    /// <summary>Stores a profile once. Returns the stored one, which is the earlier one if it was already set.</summary>
    public async Task<StoryProfile> SetProfileAsync(Guid characterId, StoryProfile profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        await using (var connection = await database.OpenAsync(ct).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE character SET story_json = COALESCE(story_json, $json) WHERE id = $id;";
            command.Parameters.AddWithValue("$id", characterId.ToString());
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(profile, Json));

            if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
            {
                throw new InvalidOperationException($"No character with id {characterId}.");
            }
        }

        return (await GetProfileAsync(characterId, ct).ConfigureAwait(false))!;
    }

    // -------------------------------------------------------------------- schedules

    public async Task SaveScheduleAsync(SaveId saveId, CharacterSchedule schedule, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO schedule (save_id, character_id, entries_json) VALUES ($save, $character, $entries)
            ON CONFLICT(character_id) DO UPDATE SET entries_json = excluded.entries_json;
            """;
        command.Parameters.AddWithValue("$save", saveId.ToString());
        command.Parameters.AddWithValue("$character", schedule.CharacterId);
        command.Parameters.AddWithValue("$entries", JsonSerializer.Serialize(schedule.Entries, Json));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, CharacterSchedule>> GetSchedulesAsync(SaveId saveId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT character_id, entries_json FROM schedule WHERE save_id = $save;";
        command.Parameters.AddWithValue("$save", saveId.ToString());

        var schedules = new Dictionary<string, CharacterSchedule>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var characterId = reader.GetString(0);
            schedules[characterId] = new CharacterSchedule(
                characterId,
                JsonSerializer.Deserialize<List<ScheduleEntry>>(reader.GetString(1), Json)!);
        }

        return schedules;
    }

    // -------------------------------------------------------------------- promises

    public async Task AddPromiseAsync(SaveId saveId, Promise promise, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(promise);

        if (promise.Status is not PromiseStatus.Open)
        {
            throw new ArgumentException("A promise is made open.", nameof(promise));
        }

        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO promise (save_id, id, character_id, kind, made_day, due_day, due_slot, place_id)
            VALUES ($save, $id, $character, $kind, $made, $due, $slot, $place);
            """;
        command.Parameters.AddWithValue("$save", saveId.ToString());
        command.Parameters.AddWithValue("$id", promise.Id);
        command.Parameters.AddWithValue("$character", promise.CharacterId);
        command.Parameters.AddWithValue("$kind", promise.Kind.ToString());
        command.Parameters.AddWithValue("$made", promise.MadeDay);
        command.Parameters.AddWithValue("$due", promise.DueDay);
        command.Parameters.AddWithValue("$slot", (object?)promise.DueSlot?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$place", (object?)promise.PlaceId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Promise>> GetPromisesAsync(SaveId saveId, bool openOnly = true, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT id, character_id, kind, made_day, due_day, due_slot, place_id, status
            FROM promise
            WHERE save_id = $save AND ($open = 0 OR status = 'Open')
            ORDER BY due_day, rowid;
            """;
        command.Parameters.AddWithValue("$save", saveId.ToString());
        command.Parameters.AddWithValue("$open", openOnly ? 1 : 0);

        var promises = new List<Promise>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            promises.Add(new Promise(
                reader.GetString(0),
                reader.GetString(1),
                Enum.Parse<PromiseKind>(reader.GetString(2)),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.IsDBNull(5) ? null : Enum.Parse<TimeOfDay>(reader.GetString(5)),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                Enum.Parse<PromiseStatus>(reader.GetString(7))));
        }

        return promises;
    }

    /// <summary>Keeps or breaks an open promise. Refused if it is not open, so it is resolved once.</summary>
    public async Task ResolvePromiseAsync(SaveId saveId, string promiseId, PromiseStatus status, int day, CancellationToken ct = default)
    {
        if (status is PromiseStatus.Open)
        {
            throw new ArgumentException("A promise is resolved as kept or broken.", nameof(status));
        }

        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            UPDATE promise SET status = $status, resolved_day = $day
            WHERE save_id = $save AND id = $id AND status = 'Open';
            """;
        command.Parameters.AddWithValue("$save", saveId.ToString());
        command.Parameters.AddWithValue("$id", promiseId);
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$day", day);

        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
        {
            throw new InvalidOperationException($"Save '{saveId}' has no open promise '{promiseId}'.");
        }
    }

    // -------------------------------------------------------------------- turn log

    /// <summary>Appends what happened in a turn, so a playthrough can be replayed (plan §8).</summary>
    public async Task LogTurnAsync(SaveId saveId, ClockState clock, string kind, object payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(payload);

        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO turn_log (save_id, day, slot, kind, payload_json, created_utc)
            VALUES ($save, $day, $slot, $kind, $payload, $created);
            """;
        command.Parameters.AddWithValue("$save", saveId.ToString());
        command.Parameters.AddWithValue("$day", clock.Day);
        command.Parameters.AddWithValue("$slot", clock.Slot.ToString());
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(payload, payload.GetType(), Json));
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
