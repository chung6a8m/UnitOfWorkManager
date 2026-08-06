using System.Data.Common;
using RepoDb;
using RepoDb.Contexts.Cachers;
using RepoDb.Interfaces;
using UnitOfWork.Core;
using UnitOfWork.Core.Exceptions;
using Xunit;

namespace UnitOfWork.RepoDb.ProviderTests.Shared;

public static class RepoDbProviderContract
{
    public static async Task ColdCacheInsertAsync(IRepoDbProviderHarness harness)
    {
        await harness.ResetSchemaAsync();
        var helper = new CountingDbHelper(harness.GetOfficialHelper());
        harness.RegisterMappings(helper);
        FlushRepoDbInsertCaches();
        var manager = CreateManager(harness);

        await InsertAndCommitAsync(manager, "counter", 10);

        Assert.Equal(1, helper.GetInvocationCount("counter"));
        Assert.Equal(1, await harness.CountRowsAsync("counter"));
    }

    public static async Task HotCacheInsertAsync(IRepoDbProviderHarness harness)
    {
        await harness.ResetSchemaAsync();
        var helper = new CountingDbHelper(harness.GetOfficialHelper());
        harness.RegisterMappings(helper);
        FlushRepoDbInsertCaches();
        var manager = CreateManager(harness);

        await InsertAndCommitAsync(manager, "counter", 10);
        await InsertAndCommitAsync(manager, "counter", 20);

        Assert.Equal(1, helper.GetInvocationCount("counter"));
        Assert.Equal(2, await harness.CountRowsAsync("counter"));
    }

    public static async Task TwoTablesDiscoveryAsync(IRepoDbProviderHarness harness)
    {
        await harness.ResetSchemaAsync();
        var helper = new CountingDbHelper(harness.GetOfficialHelper());
        harness.RegisterMappings(helper);
        FlushRepoDbInsertCaches();
        var manager = CreateManager(harness);

        await using (var scope = await manager.BeginAsync())
        {
            await scope.Connection.InsertAsync("counter", new { value = 10 });
            await scope.Connection.InsertAsync("counter_archive", new { value = 20 });
            await scope.CompleteAsync();
        }

        Assert.Equal(1, helper.GetInvocationCount("counter"));
        Assert.Equal(1, helper.GetInvocationCount("counter_archive"));
        Assert.Equal(1, await harness.CountRowsAsync("counter"));
        Assert.Equal(1, await harness.CountRowsAsync("counter_archive"));
    }

    public static async Task IdentityInsertAsync(IRepoDbProviderHarness harness)
    {
        await harness.ResetSchemaAsync();
        harness.RegisterMappings(harness.GetOfficialHelper());
        FlushRepoDbInsertCaches();
        var manager = CreateManager(harness);
        long identity;

        await using (var scope = await manager.BeginAsync())
        {
            var value = await scope.Connection.InsertAsync(
                "counter",
                new { value = 42 });
            identity = Convert.ToInt64(value);
            await scope.CompleteAsync();
        }

        Assert.True(identity > 0, $"{harness.ProviderName} returned identity {identity}.");
        Assert.Equal(1, await harness.CountRowsAsync("counter"));
    }

    public static async Task CancelledDiscoveryReleasesLeaseAsync(
        IRepoDbProviderHarness harness)
    {
        await harness.ResetSchemaAsync();
        var officialHelper = harness.GetOfficialHelper();
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
                command.CommandText = harness.ScalarOneSql;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                Assert.True(await reader.ReadAsync(cancellationToken));
                operationStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        };
        harness.RegisterMappings(injectedHelper);
        FlushRepoDbInsertCaches();
        var manager = CreateManager(harness);
        await using var scope = await manager.BeginAsync();
        using var cancellation = new CancellationTokenSource();

        var discoveryTask = injectedHelper.GetFieldsAsync(
            scope.Connection,
            "counter",
            cancellationToken: cancellation.Token);
        await operationStarted.Task;
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await discoveryTask);
        Assert.IsNotType<UnitOfWorkConcurrencyException>(exception);

        harness.RegisterMappings(officialHelper);
        Assert.Equal(1L, await ExecuteScalarOneAsync(scope.Connection, harness.ScalarOneSql));
        await scope.RollbackAsync();
    }

    public static async Task FailedDiscoveryReleasesLeaseAsync(
        IRepoDbProviderHarness harness)
    {
        await harness.ResetSchemaAsync();
        var officialHelper = harness.GetOfficialHelper();
        var injectedHelper = new InjectedDbHelper(officialHelper)
        {
            BeforeGetFieldsAsync = async (
                connection,
                _,
                _,
                cancellationToken) =>
            {
                await using var command = ((DbConnection)connection).CreateCommand();
                command.CommandText = harness.ScalarOneSql;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                Assert.True(await reader.ReadAsync(cancellationToken));
                throw new InvalidOperationException("Injected metadata failure");
            }
        };
        harness.RegisterMappings(injectedHelper);
        FlushRepoDbInsertCaches();
        var manager = CreateManager(harness);
        await using var scope = await manager.BeginAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await injectedHelper.GetFieldsAsync(
                scope.Connection,
                "counter"));
        Assert.Equal("Injected metadata failure", exception.Message);

        harness.RegisterMappings(officialHelper);
        Assert.Equal(1L, await ExecuteScalarOneAsync(scope.Connection, harness.ScalarOneSql));
        await scope.RollbackAsync();
    }

    public static async Task SameRootOverlapIsRejectedAsync(
        IRepoDbProviderHarness harness)
    {
        await harness.ResetSchemaAsync();
        harness.RegisterMappings(harness.GetOfficialHelper());
        FlushRepoDbInsertCaches();
        var manager = CreateManager(harness);
        await using var scope = await manager.BeginAsync();
        await using var command = scope.Connection.CreateCommand();
        command.CommandText = "SELECT id, value FROM counter;";
        await using var reader = await command.ExecuteReaderAsync();

        await Assert.ThrowsAsync<UnitOfWorkConcurrencyException>(
            async () => await scope.Connection.InsertAsync(
                "counter",
                new { value = 10 }));

        await reader.DisposeAsync();
        await scope.Connection.InsertAsync("counter", new { value = 20 });
        await scope.CompleteAsync();

        Assert.Equal(1, await harness.CountRowsAsync("counter"));
    }

    public static async Task IndependentRootsCanInsertConcurrentlyAsync(
        IRepoDbProviderHarness harness)
    {
        await harness.ResetSchemaAsync();
        harness.RegisterMappings(harness.GetOfficialHelper());
        FlushRepoDbInsertCaches();
        var firstManager = CreateManager(harness);
        var secondManager = CreateManager(harness);
        var allReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;

        async Task InsertAsync(UnitOfWorkManager manager, int value)
        {
            await using var scope = await manager.BeginAsync();
            if (Interlocked.Increment(ref readyCount) == 2)
                allReady.TrySetResult();

            await release.Task;
            await scope.Connection.InsertAsync("counter", new { value });
            await scope.CompleteAsync();
        }

        var firstInsert = InsertAsync(firstManager, 10);
        var secondInsert = InsertAsync(secondManager, 20);
        await allReady.Task;
        release.TrySetResult();
        await Task.WhenAll(firstInsert, secondInsert);

        Assert.Equal(2, await harness.CountRowsAsync("counter"));
    }

    private static UnitOfWorkManager CreateManager(IRepoDbProviderHarness harness) =>
        new(harness.CreateConnectionFactory(), static (_, _) => new object());

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

    private static async Task<long> ExecuteScalarOneAsync(
        DbConnection connection,
        string scalarOneSql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = scalarOneSql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
