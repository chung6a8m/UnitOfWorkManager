using System.Data.Common;
using MySql.Data.MySqlClient;
using RepoDb;
using RepoDb.Interfaces;
using UnitOfWork.Core;
using UnitOfWork.RepoDb.ProviderTests.Shared;

namespace UnitOfWork.RepoDb.MySql.Tests;

internal sealed class MySqlProviderHarness : IRepoDbProviderHarness
{
    private readonly string _connectionString =
        Environment.GetEnvironmentVariable("UOW_TEST_MYSQL")
        ?? throw new InvalidOperationException("UOW_TEST_MYSQL is not set.");

    public string ProviderName => "MySql.Data";

    public string ScalarOneSql => "SELECT 1;";

    public IDbHelper GetOfficialHelper()
    {
        GlobalConfiguration.Setup().UseMySql();
        return DbHelperMapper.Get<MySqlConnection>();
    }

    public void RegisterMappings(IDbHelper helper)
    {
        GlobalConfiguration.Setup().UseMySql();
        DbSettingMapper.Add<TransactionBoundDbConnection>(
            DbSettingMapper.Get<MySqlConnection>(),
            true);
        DbHelperMapper.Add<TransactionBoundDbConnection>(helper, true);
        StatementBuilderMapper.Add<TransactionBoundDbConnection>(
            StatementBuilderMapper.Get<MySqlConnection>(),
            true);
    }

    public IDbConnectionFactory CreateConnectionFactory() =>
        new MySqlConnectionFactory(_connectionString);

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

    private sealed class MySqlConnectionFactory : IDbConnectionFactory
    {
        private readonly string _connectionString;

        public MySqlConnectionFactory(string connectionString)
        {
            _connectionString = connectionString;
        }

        public DbConnection CreateConnection() => new MySqlConnection(_connectionString);
    }
}
