using System.Data.Common;
using Microsoft.Data.Sqlite;
using RepoDb;
using RepoDb.Contexts.Cachers;
using RepoDb.Interfaces;
using UnitOfWork.Core;
using UnitOfWork.Sample.WebApi.MinimalApi.Infrastructure;
using UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests.Fixtures;
using Xunit;

namespace UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests;

public sealed class RepoDbSqliteMetadataTests
{
    [Fact]
    public async Task Rsql01_Cold_Cache_Insert_Uses_Transaction_Bound_Metadata_Helper()
    {
        using var database = new RepoDbSqliteTestDatabase();
        var helper = new CountingDbHelper(CreateOfficialHelper());
        RegisterMappings(helper);
        FlushRepoDbInsertCaches();
        var manager = CreateManager(database);

        await using (var scope = await manager.BeginAsync())
        {
            await scope.Connection.InsertAsync("counter", new { value = 10 });
            await scope.CompleteAsync();
        }

        Assert.Equal(1, helper.GetInvocationCount("counter"));
        Assert.Equal(1, await database.CountAsync("counter"));
    }

    [Fact]
    public async Task Rsql02_Hot_Cache_Insert_Does_Not_Invoke_Helper_A_Second_Time()
    {
        using var database = new RepoDbSqliteTestDatabase();
        var helper = new CountingDbHelper(CreateOfficialHelper());
        RegisterMappings(helper);
        FlushRepoDbInsertCaches();
        var manager = CreateManager(database);

        await InsertAndCommitAsync(manager, "counter", 10);
        await InsertAndCommitAsync(manager, "counter", 20);

        Assert.Equal(1, helper.GetInvocationCount("counter"));
        Assert.Equal(2, await database.CountAsync("counter"));
    }

    [Fact]
    public async Task Rsql03_Two_Tables_Are_Discovered_Once_Each_And_Sequentially()
    {
        using var database = new RepoDbSqliteTestDatabase();
        var helper = new CountingDbHelper(CreateOfficialHelper());
        RegisterMappings(helper);
        FlushRepoDbInsertCaches();
        var manager = CreateManager(database);

        await using (var scope = await manager.BeginAsync())
        {
            await scope.Connection.InsertAsync("counter", new { value = 10 });
            await scope.Connection.InsertAsync("counter_archive", new { value = 20 });
            await scope.CompleteAsync();
        }

        Assert.Equal(1, helper.GetInvocationCount("counter"));
        Assert.Equal(1, helper.GetInvocationCount("counter_archive"));
        Assert.Equal(1, await database.CountAsync("counter"));
        Assert.Equal(1, await database.CountAsync("counter_archive"));
    }

    [Fact]
    public async Task Rsql04_Insert_Returns_Positive_Identity_And_Persists_Row()
    {
        using var database = new RepoDbSqliteTestDatabase();
        RegisterMappings(CreateOfficialHelper());
        FlushRepoDbInsertCaches();
        var manager = CreateManager(database);
        long identity;

        await using (var scope = await manager.BeginAsync())
        {
            var value = await scope.Connection.InsertAsync(
                "counter",
                new { value = 42 });
            identity = Convert.ToInt64(value);
            await scope.CompleteAsync();
        }

        Assert.True(identity > 0);
        Assert.Equal(1, await database.CountAsync("counter"));
    }

    [Fact]
    public async Task Rsql05_Cancelled_Metadata_Callback_Releases_Reader_Lease()
    {
        using var database = new RepoDbSqliteTestDatabase();
        var officialHelper = CreateOfficialHelper();
        var operationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var injectedHelper = new InjectedDbHelper(officialHelper)
        {
            BeforeGetFieldsAsync = async (
                connection,
                _,
                _,
                cancellationToken) =>
            {
                await using var command = ((DbConnection)connection).CreateCommand();
                command.CommandText = "SELECT 1;";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                Assert.True(await reader.ReadAsync(cancellationToken));
                operationStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        };
        RegisterMappings(injectedHelper);
        FlushRepoDbInsertCaches();
        var manager = CreateManager(database);
        await using var scope = await manager.BeginAsync();
        using var cancellation = new CancellationTokenSource();

        var discoveryTask = injectedHelper.GetFieldsAsync(
            scope.Connection,
            "counter",
            cancellationToken: cancellation.Token);
        await operationStarted.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await discoveryTask);

        RegisterMappings(officialHelper);
        Assert.Equal(1L, await ExecuteScalarOneAsync(scope.Connection));
        await scope.RollbackAsync();
    }

    [Fact]
    public async Task Rsql06_Failed_Metadata_Callback_Releases_Reader_Lease()
    {
        using var database = new RepoDbSqliteTestDatabase();
        var officialHelper = CreateOfficialHelper();
        var injectedHelper = new InjectedDbHelper(officialHelper)
        {
            BeforeGetFieldsAsync = async (
                connection,
                _,
                _,
                cancellationToken) =>
            {
                await using var command = ((DbConnection)connection).CreateCommand();
                command.CommandText = "SELECT 1;";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                Assert.True(await reader.ReadAsync(cancellationToken));
                throw new InvalidOperationException("Injected metadata failure");
            }
        };
        RegisterMappings(injectedHelper);
        FlushRepoDbInsertCaches();
        var manager = CreateManager(database);
        await using var scope = await manager.BeginAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await injectedHelper.GetFieldsAsync(
                scope.Connection,
                "counter"));
        Assert.Equal("Injected metadata failure", exception.Message);

        RegisterMappings(officialHelper);
        Assert.Equal(1L, await ExecuteScalarOneAsync(scope.Connection));
        await scope.RollbackAsync();
    }

    private static UnitOfWorkManager CreateManager(IDbConnectionFactory database) =>
        new(database, static (_, _) => new object());

    private static IDbHelper CreateOfficialHelper()
    {
        GlobalConfiguration.Setup().UseSqlite();
        return new TransactionBoundSqliteDbHelper(
            DbHelperMapper.Get<SqliteConnection>());
    }

    private static void RegisterMappings(IDbHelper helper)
    {
        GlobalConfiguration.Setup().UseSqlite();
        DbSettingMapper.Add<TransactionBoundDbConnection>(
            DbSettingMapper.Get<SqliteConnection>(),
            true);
        DbHelperMapper.Add<TransactionBoundDbConnection>(helper, true);
        StatementBuilderMapper.Add<TransactionBoundDbConnection>(
            StatementBuilderMapper.Get<SqliteConnection>(),
            true);
    }

    private static void FlushRepoDbInsertCaches()
    {
        InsertExecutionContextCache.Flush();
        DbFieldCache.Flush();
    }

    private static async Task InsertAndCommitAsync(
        UnitOfWorkManager manager,
        string tableName,
        int value)
    {
        await using var scope = await manager.BeginAsync();
        await scope.Connection.InsertAsync(tableName, new { value });
        await scope.CompleteAsync();
    }

    private static async Task<long> ExecuteScalarOneAsync(DbConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1;";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
