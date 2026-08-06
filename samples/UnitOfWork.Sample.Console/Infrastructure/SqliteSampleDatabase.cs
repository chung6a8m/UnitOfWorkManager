using System.Data.Common;
using Microsoft.Data.Sqlite;
using UnitOfWork.Core;

namespace UnitOfWork.Sample.ConsoleApp.Infrastructure;

public sealed class SqliteSampleDatabase : IDbConnectionFactory, IAsyncDisposable
{
    private readonly string _databasePath;
    private readonly string _connectionString;

    public SqliteSampleDatabase()
    {
        _databasePath = Path.Combine(
            Path.GetTempPath(),
            $"uow_console_sample_{Guid.NewGuid():N}.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Pooling = false
        }.ToString();

        InitializeSchema();
    }

    public DbConnection CreateConnection() => new SqliteConnection(_connectionString);

    public async Task<IReadOnlyList<int>> ReadValuesAsync(
        CancellationToken cancellationToken = default)
    {
        var values = new List<int>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Counter ORDER BY Id;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            values.Add(reader.GetInt32(0));

        return values;
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();

        DeleteWithRetry(_databasePath);
        DeleteWithRetry(_databasePath + "-journal");
        DeleteWithRetry(_databasePath + "-wal");
        DeleteWithRetry(_databasePath + "-shm");

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
