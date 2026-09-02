using Game.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Game.Data.Tests;

/// <summary>
/// A migrated database in a throwaway directory.
/// </summary>
/// <remarks>
/// Deliberately a real file rather than <c>:memory:</c>. The things most worth testing here
/// are WAL mode, foreign key enforcement and migration bookkeeping, none of which behave
/// identically for an in-memory database.
/// </remarks>
internal sealed class TempDatabase : IDisposable
{
    private readonly string _directory;

    public TempDatabase()
    {
        _directory = Path.Combine(Path.GetTempPath(), "adate-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        Database = new Database(Options.Create(new DatabaseOptions
        {
            Path = Path.Combine(_directory, "test.db"),
        }));

        Database.Migrate();
    }

    public Database Database { get; }

    public T Scalar<T>(string sql)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }

    public void Dispose()
    {
        // Pooled connections keep the file handle open, which makes the directory
        // undeletable on Windows until the pool is cleared.
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory is not worth failing a test run over.
        }
    }
}
