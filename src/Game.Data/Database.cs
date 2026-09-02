using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Game.Data;

/// <summary>
/// Owns the connection string and schema migration. Everything else in this assembly
/// takes connections from here rather than building its own.
/// </summary>
public sealed class Database
{
    private readonly string _connectionString;

    public Database(IOptions<DatabaseOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var path = Path.GetFullPath(options.Value.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Pooling keeps the WAL and the busy timeout from being re-established on
            // every open, which matters because Blazor Server opens connections per
            // circuit interaction rather than per request.
            Pooling = true,
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        Configure(connection);
        return connection;
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        Configure(connection);
        return connection;
    }

    private static void Configure(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();

        // WAL lets the scene viewer read while a generation writes, instead of blocking
        // on it. NORMAL synchronous is the usual companion: under WAL it only risks
        // losing the last transaction on an OS crash, not corruption.
        //
        // foreign_keys is OFF by default in SQLite and must be set per connection. The
        // schema's ON DELETE CASCADE rules do nothing without it.
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
            """;

        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Applies any migration the database has not seen, in filename order. Migrations are
    /// embedded resources named <c>Migrations.NNN_name.sql</c>.
    /// </summary>
    public void Migrate()
    {
        using var connection = Open();

        using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_migration (
                    name        TEXT PRIMARY KEY,
                    applied_utc TEXT NOT NULL
                ) STRICT;
                """;
            create.ExecuteNonQuery();
        }

        var applied = new HashSet<string>(StringComparer.Ordinal);
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT name FROM schema_migration;";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                applied.Add(reader.GetString(0));
            }
        }

        foreach (var (name, sql) in LoadMigrations())
        {
            if (!applied.Add(name))
            {
                continue;
            }

            // One transaction per migration. A migration that fails half-applied would
            // leave a schema no later migration could reason about.
            using var transaction = connection.BeginTransaction();

            using (var apply = connection.CreateCommand())
            {
                apply.Transaction = transaction;
                apply.CommandText = sql;
                apply.ExecuteNonQuery();
            }

            using (var record = connection.CreateCommand())
            {
                record.Transaction = transaction;
                record.CommandText =
                    "INSERT INTO schema_migration (name, applied_utc) VALUES ($name, $now);";
                record.Parameters.AddWithValue("$name", name);
                record.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                record.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    /// <summary>Ordered by name, which is why migrations are numbered rather than named.</summary>
    private static IEnumerable<(string Name, string Sql)> LoadMigrations()
    {
        var assembly = typeof(Database).Assembly;

        var names = assembly.GetManifestResourceNames()
            .Where(static n => n.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);

        foreach (var resource in names)
        {
            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Migration resource '{resource}' could not be opened.");

            using var reader = new StreamReader(stream);
            yield return (ShortName(resource), reader.ReadToEnd());
        }
    }

    /// <summary>
    /// Strips the assembly and folder prefix so the recorded name survives the assembly
    /// being renamed. A rename would otherwise look like a set of unapplied migrations.
    /// </summary>
    private static string ShortName(string resourceName) =>
        resourceName[(resourceName.LastIndexOf('.', resourceName.Length - 5) + 1)..];
}

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public string Path { get; set; } = System.IO.Path.Combine("data", "adate.db");
}
