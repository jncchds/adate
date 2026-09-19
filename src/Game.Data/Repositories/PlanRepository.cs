using Game.Core.Saves;
using Game.Core.Scenes;
using Game.Core.Settings;
using Microsoft.Data.Sqlite;

namespace Game.Data.Repositories;

/// <summary>
/// One save's own calendar and the beats rewritten for its places (migration 018). The places
/// themselves live in <c>place</c>; this is what is left of a plan once they are stored.
/// </summary>
public sealed class PlanRepository(Database database)
{
    /// <summary>
    /// Records the plan for a save, once. False when the save already has one, in which case nothing is
    /// written: a plan is what the rest of the game has been reading and cannot be laid out twice.
    /// </summary>
    /// <param name="planned">Whether a model laid it out, or the setting is being played as authored.</param>
    public async Task<bool> SaveAsync(
        SaveId saveId,
        bool planned,
        IEnumerable<SettingEvent> events,
        IReadOnlyDictionary<string, string> encounterTexts,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(encounterTexts);

        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var claim = connection.CreateCommand())
        {
            claim.Transaction = transaction;
            claim.CommandText = "INSERT INTO save_plan (save_id, planned) VALUES ($save, $planned) ON CONFLICT(save_id) DO NOTHING;";
            claim.Parameters.AddWithValue("$save", saveId.ToString());
            claim.Parameters.AddWithValue("$planned", planned ? 1 : 0);

            if (await claim.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
            {
                return false;
            }
        }

        foreach (var ev in events)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO save_event (save_id, id, name, day, place_id, slot)
                VALUES ($save, $id, $name, $day, $place, $slot);
                """;

            command.Parameters.AddWithValue("$save", saveId.ToString());
            command.Parameters.AddWithValue("$id", ev.Id);
            command.Parameters.AddWithValue("$name", ev.Name);
            command.Parameters.AddWithValue("$day", ev.Day);
            command.Parameters.AddWithValue("$place", ev.Place);
            command.Parameters.AddWithValue("$slot", ev.Time.ToString());
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var (id, text) in encounterTexts)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO save_encounter_text (save_id, id, text) VALUES ($save, $id, $text);";
            command.Parameters.AddWithValue("$save", saveId.ToString());
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$text", text);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Whether this save has been laid out, whether or not a model did it.</summary>
    public async Task<bool> IsPlannedAsync(SaveId saveId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT 1 FROM save_plan WHERE save_id = $save;";
        command.Parameters.AddWithValue("$save", saveId.ToString());

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    /// <summary>This save's dated events, earliest first; empty for a save laid out before planning, which plays the setting's own.</summary>
    public async Task<IReadOnlyList<SettingEvent>> EventsAsync(SaveId saveId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT id, name, day, place_id, slot FROM save_event WHERE save_id = $save ORDER BY day, id;";
        command.Parameters.AddWithValue("$save", saveId.ToString());

        var events = new List<SettingEvent>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            events.Add(new SettingEvent(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                Enum.Parse<TimeOfDay>(reader.GetString(4))));
        }

        return events;
    }

    /// <summary>The beats this save rewrote, by encounter id; empty when none were.</summary>
    public async Task<IReadOnlyDictionary<string, string>> EncounterTextsAsync(SaveId saveId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT id, text FROM save_encounter_text WHERE save_id = $save;";
        command.Parameters.AddWithValue("$save", saveId.ToString());

        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            texts[reader.GetString(0)] = reader.GetString(1);
        }

        return texts;
    }
}
