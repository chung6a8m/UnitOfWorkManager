using System.Data.Common;
using MySqlConnector;
using RepoDb;
using RepoDb.Interfaces;
using UnitOfWork.Core;
using UnitOfWork.RepoDb.ProviderTests.Shared;

namespace UnitOfWork.RepoDb.MySqlConnector.Tests;

internal sealed class MySqlConnectorProviderHarness : IRepoDbProviderHarness
{
    private readonly string _connectionString =
        Environment.GetEnvironmentVariable("UOW_TEST_MYSQL")
        ?? throw new InvalidOperationException("UOW_TEST_MYSQL is not set.");

    public string ProviderName => "MySqlConnector";

    public string ScalarOneSql => "SELECT 1;";

    public IDbHelper GetOfficialHelper()
    {
        GlobalConfiguration.Setup().UseMySqlConnector();
        return DbHelperMapper.Get<MySqlConnection>();
    }

    public void RegisterMappings(IDbHelper helper)
    {
        GlobalConfiguration.Setup().UseMySqlConnector();
        DbSettingMapper.Add<TransactionBoundDbConnection>(
            DbSettingMapper.Get<MySqlConnection>(),
            true);
        DbHelperMapper.Add<TransactionBoundDbConnection>(helper, true);
        StatementBuilderMapper.Add<TransactionBoundDbConnection>(
            StatementBuilderMapper.Get<MySqlConnection>(),
            true);
    }

    public IDbConnectionFactory CreateConnectionFactory() =>
        new MySqlConnectorConnectionFactory(_connectionString);

    public async Task ResetSchemaAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await ExecuteAsync(connection, "DROP TABLE IF EXISTS counter_archive;", cancellationToken);
        await ExecuteAsync(connection, "DROP TABLE IF EXISTS counter;", cancellationToken);
        await ExecuteAsync(connection,
            "CREATE TABLE counter (id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY, value INT NOT NULL) ENGINE=InnoDB;",
            cancellationToken);
        await ExecuteAsync(connection,
            "CREATE TABLE counter_archive (id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY, value INT NOT NULL) ENGINE=InnoDB;",
            cancellationToken);
    }

    public async Task<long> CountRowsAsync(
        string tableName,
        CancellationToken cancellationToken = default)
    {
        var normalizedTable = NormalizeTableName(tableName);
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {normalizedTable};";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async Task ExecuteAsync(
        MySqlConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string NormalizeTableName(string tableName) => tableName switch
    {
        "counter" => "counter",
        "counter_archive" => "counter_archive",
        _ => throw new ArgumentOutOfRangeException(nameof(tableName), tableName, "Unknown test table.")
    };

    private sealed class MySqlConnectorConnectionFactory : IDbConnectionFactory
    {
        private readonly string _connectionString;

        public MySqlConnectorConnectionFactory(string connectionString)
        {
            _connectionString = connectionString;
        }

        public DbConnection CreateConnection() => new MySqlConnection(_connectionString);
    }
}
