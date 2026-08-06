using System.Data.Common;
using Microsoft.Data.Sqlite;
using UnitOfWork.Core;

namespace UnitOfWork.Sample.WebApi.Infrastructure;

public sealed class SqliteSampleDatabase : IDbConnectionFactory, IDisposable, IAsyncDisposable
{
    private readonly string _databasePath;
    private readonly string _connectionString;
    private int _disposed;

    public SqliteSampleDatabase()
    {
        _databasePath = Path.Combine(
            Path.GetTempPath(),
            $"uow_webapi_sample_{Guid.NewGuid():N}.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Pooling = false
        }.ToString();

        InitializeSchema();
    }

    public DbConnection CreateConnection() => new SqliteConnection(_connectionString);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        SqliteConnection.ClearAllPools();
        DeleteWithRetry(_databasePath);
        DeleteWithRetry(_databasePath + "-journal");
        DeleteWithRetry(_databasePath + "-wal");
        DeleteWithRetry(_databasePath + "-shm");
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void InitializeSchema()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE Counter
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Value INTEGER NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private static void DeleteWithRetry(
        string path,
        int attempts = 10,
        int delayMilliseconds = 100)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);

                return;
            }
            catch (IOException) when (attempt < attempts - 1)
            {
                Thread.Sleep(delayMilliseconds);
            }
        }
    }
}
