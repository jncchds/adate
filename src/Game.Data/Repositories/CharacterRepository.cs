using System.Text.Json;
using Game.Core.Characters;
using Game.Core.Saves;
using Microsoft.Data.Sqlite;

namespace Game.Data.Repositories;

public sealed class CharacterRepository(Database database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<CharacterRecord> CreateAsync(
        SaveId saveId,
        CharacterAppearance appearance,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(appearance);

        // Validated before it reaches the database, so the failure names the offending
        // attribute rather than surfacing as a CHECK constraint violation.
        appearance.Validate();

        var record = new CharacterRecord(
            Guid.CreateVersion7(),
            saveId,
            appearance,
            AnchorImageHash: null,
            AnchorSeed: null,
            LoraPath: null);

        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO character (id, save_id, age, appearance_json)
            VALUES ($id, $save, $age, $appearance);
            """;

        command.Parameters.AddWithValue("$id", record.Id.ToString());
        command.Parameters.AddWithValue("$save", saveId.ToString());
        command.Parameters.AddWithValue("$age", appearance.Age);
        command.Parameters.AddWithValue("$appearance", JsonSerializer.Serialize(appearance, Json));

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return record;
    }

    /// <summary>
    /// Records the approved portrait as the character's anchor. Every sprite is then
    /// generated against this image, which is what makes the expressions read as one
    /// person. The seed is stored alongside it so the portrait itself stays reproducible.
    /// </summary>
    public async Task SetAnchorAsync(
        Guid characterId,
        string anchorImageHash,
        long anchorSeed,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(anchorImageHash);

        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            UPDATE character
            SET anchor_image_hash = $hash, anchor_seed = $seed
            WHERE id = $id;
            """;

        command.Parameters.AddWithValue("$hash", anchorImageHash);
        command.Parameters.AddWithValue("$seed", anchorSeed);
        command.Parameters.AddWithValue("$id", characterId.ToString());

        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
        {
            throw new InvalidOperationException($"No character with id '{characterId}'.");
        }
    }

    public async Task<CharacterRecord?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT id, save_id, appearance_json, anchor_image_hash, anchor_seed, lora_path
            FROM character
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString());

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<IReadOnlyList<CharacterRecord>> ListAsync(SaveId saveId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT id, save_id, appearance_json, anchor_image_hash, anchor_seed, lora_path
            FROM character
            WHERE save_id = $save
            ORDER BY id;
            """;
        command.Parameters.AddWithValue("$save", saveId.ToString());

        var characters = new List<CharacterRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            characters.Add(Map(reader));
        }

        return characters;
    }

    private static CharacterRecord Map(SqliteDataReader reader)
    {
        var appearance = JsonSerializer.Deserialize<CharacterAppearance>(reader.GetString(2), Json)
            ?? throw new InvalidOperationException("Stored appearance_json deserialised to null.");

        return new CharacterRecord(
            Guid.Parse(reader.GetString(0)),
            SaveId.Parse(reader.GetString(1)),
            appearance,
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }
}
