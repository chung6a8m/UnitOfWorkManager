using System.Data.Common;
using Microsoft.Data.Sqlite;
using UnitOfWork.Core;

namespace UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests.Fixtures;

internal sealed class RepoDbSqliteTestDatabase : IDbConnectionFactory, IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"uow_repodb_{Guid.NewGuid():N}.db");

    public RepoDbSqliteTestDatabase()
    {
        using var connection = new SqliteConnection(BuildConnectionString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE counter (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                value INTEGER NOT NULL
            );
            CREATE TABLE counter_archive (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                value INTEGER NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    public DbConnection CreateConnection()
    {
        var connection = new SqliteConnection(BuildConnectionString());
        connection.Open();
        return connection;
    }

    public async Task<long> CountAsync(
        string tableName,
        CancellationToken cancellationToken = default)
    {
        var normalizedTable = NormalizeTableName(tableName);
        await using var connection = new SqliteConnection(BuildConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {normalizedTable};";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(value);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        DeleteWithRetry(_databasePath);
        DeleteWithRetry(_databasePath + "-journal");
        DeleteWithRetry(_databasePath + "-wal");
        DeleteWithRetry(_databasePath + "-shm");
    }

    private string BuildConnectionString() =>
        $"Data Source={_databasePath};Pooling=False;";

    private static string NormalizeTableName(string tableName) => tableName switch
    {
        "counter" => "counter",
        "counter_archive" => "counter_archive",
        _ => throw new ArgumentOutOfRangeException(
            nameof(tableName),
            tableName,
            "Unknown test table.")
    };

    private static void DeleteWithRetry(
        string path,
        int attempts = 10,
        int delayMilliseconds = 150)
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
