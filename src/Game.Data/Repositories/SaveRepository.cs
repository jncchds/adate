using Game.Core;
using Game.Core.Saves;
using Microsoft.Data.Sqlite;

namespace Game.Data.Repositories;

public sealed class SaveRepository(Database database)
{
    /// <param name="packFingerprint">
    /// Hash of the style pack manifest as it stood at creation. A pack is locked per save
    /// (HANDOFF 1.5); storing the fingerprint is what lets the game notice the manifest
    /// being edited underneath an existing save rather than silently generating art that
    /// no longer matches what is already cached.
    /// </param>
    /// <param name="playerName">Used only in the writing; the player is never drawn.</param>
    /// <param name="playerGender">Used only in the writing, for how the story refers to the player.</param>
    /// <param name="narrationLanguage">The language the story is written in; null for English.</param>
    public async Task<SaveRecord> CreateAsync(
        string stylePackId,
        string packFingerprint,
        Ceiling ceiling,
        string? settingId = null,
        string? playerName = null,
        string? playerGender = null,
        string? narrationLanguage = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stylePackId);
        ArgumentException.ThrowIfNullOrWhiteSpace(packFingerprint);

        var record = new SaveRecord(SaveId.New(), stylePackId, ceiling, DateTimeOffset.UtcNow);

        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO save (id, style_pack_id, pack_fingerprint, ceiling, created_utc, setting_id, player_name, player_gender, narration_language)
            VALUES ($id, $pack, $fingerprint, $ceiling, $created, $setting, $playerName, $playerGender, $language);
            """;

        command.Parameters.AddWithValue("$id", record.Id.ToString());
        command.Parameters.AddWithValue("$pack", stylePackId);
        command.Parameters.AddWithValue("$fingerprint", packFingerprint);
        command.Parameters.AddWithValue("$ceiling", (int)ceiling);
        command.Parameters.AddWithValue("$created", record.CreatedUtc.ToString("O"));
        command.Parameters.AddWithValue("$setting", (object?)settingId ?? DBNull.Value);
        command.Parameters.AddWithValue("$playerName", (object?)playerName ?? DBNull.Value);
        command.Parameters.AddWithValue("$playerGender", (object?)playerGender ?? DBNull.Value);
        command.Parameters.AddWithValue("$language", (object?)narrationLanguage ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return record;
    }

    /// <summary>The language the story is written in, as typed at new game; null for saves from before languages (English).</summary>
    public async Task<string?> GetNarrationLanguageAsync(SaveId id, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT narration_language FROM save WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    public async Task<SaveRecord?> GetAsync(SaveId id, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT id, style_pack_id, ceiling, created_utc
            FROM save
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString());

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<string?> GetPackFingerprintAsync(SaveId id, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT pack_fingerprint FROM save WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    public async Task<string?> GetPlayerNameAsync(SaveId id, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT player_name FROM save WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    /// <summary>The player's gender as chosen on the form, for how the writing refers to them.</summary>
    public async Task<string?> GetPlayerGenderAsync(SaveId id, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT player_gender FROM save WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    public async Task<string?> GetSettingIdAsync(SaveId id, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT setting_id FROM save WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    /// <summary>
    /// Sets a save's setting once. A setting is locked like a style pack: its places, openings and
    /// events are what the save's rows refer to. Returns the setting the save ends up with.
    /// </summary>
    public async Task<string> SetSettingAsync(SaveId id, string settingId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingId);

        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            UPDATE save SET setting_id = COALESCE(setting_id, $setting) WHERE id = $id
            RETURNING setting_id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$setting", settingId);

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string
            ?? throw new InvalidOperationException($"No save with id '{id}'.");
    }

    /// <summary>Newest first, so the shell can offer "continue" without a second query.</summary>
    public async Task<IReadOnlyList<SaveRecord>> ListAsync(CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT id, style_pack_id, ceiling, created_utc
            FROM save
            ORDER BY created_utc DESC;
            """;

        var saves = new List<SaveRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            saves.Add(Map(reader));
        }

        return saves;
    }

    private static SaveRecord Map(SqliteDataReader reader) => new(
        SaveId.Parse(reader.GetString(0)),
        reader.GetString(1),
        (Ceiling)reader.GetInt32(2),
        DateTimeOffset.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind));
}
