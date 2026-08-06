using System.Data.Common;
using Microsoft.Data.SqlClient;
using RepoDb;
using RepoDb.Interfaces;
using UnitOfWork.Core;
using UnitOfWork.RepoDb.ProviderTests.Shared;

namespace UnitOfWork.RepoDb.SqlServer.Tests;

internal sealed class SqlServerProviderHarness : IRepoDbProviderHarness
{
    private readonly string _connectionString =
        Environment.GetEnvironmentVariable("UOW_TEST_SQLSERVER")
        ?? throw new InvalidOperationException("UOW_TEST_SQLSERVER is not set.");

    public string ProviderName => "SQL Server";

    public string ScalarOneSql => "SELECT 1;";

    public IDbHelper GetOfficialHelper()
    {
        GlobalConfiguration.Setup().UseSqlServer();
        return DbHelperMapper.Get<SqlConnection>();
    }

    public void RegisterMappings(IDbHelper helper)
    {
        GlobalConfiguration.Setup().UseSqlServer();
        DbSettingMapper.Add<TransactionBoundDbConnection>(
            DbSettingMapper.Get<SqlConnection>(),
            true);
        DbHelperMapper.Add<TransactionBoundDbConnection>(helper, true);
        StatementBuilderMapper.Add<TransactionBoundDbConnection>(
            StatementBuilderMapper.Get<SqlConnection>(),
            true);
    }

    public IDbConnectionFactory CreateConnectionFactory() =>
        new SqlServerConnectionFactory(_connectionString);

    public async Task ResetSchemaAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await ExecuteAsync(connection,
            "IF OBJECT_ID('dbo.counter_archive', 'U') IS NOT NULL DROP TABLE dbo.counter_archive;",
            cancellationToken);
        await ExecuteAsync(connection,
            "IF OBJECT_ID('dbo.counter', 'U') IS NOT NULL DROP TABLE dbo.counter;",
            cancellationToken);
        await ExecuteAsync(connection,
            "CREATE TABLE dbo.counter (id BIGINT IDENTITY(1,1) PRIMARY KEY, value INT NOT NULL);",
            cancellationToken);
        await ExecuteAsync(connection,
            "CREATE TABLE dbo.counter_archive (id BIGINT IDENTITY(1,1) PRIMARY KEY, value INT NOT NULL);",
            cancellationToken);
    }

    public async Task<long> CountRowsAsync(
        string tableName,
        CancellationToken cancellationToken = default)
    {
        var normalizedTable = NormalizeTableName(tableName);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT_BIG(*) FROM dbo.{normalizedTable};";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async Task ExecuteAsync(
        SqlConnection connection,
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

    private sealed class SqlServerConnectionFactory : IDbConnectionFactory
    {
        private readonly string _connectionString;

        public SqlServerConnectionFactory(string connectionString)
        {
            _connectionString = connectionString;
        }

        public DbConnection CreateConnection() => new SqlConnection(_connectionString);
    }
}
