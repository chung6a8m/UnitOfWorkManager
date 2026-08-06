using UnitOfWork.Core;
using UnitOfWork.Core.Exceptions;
using UnitOfWork.Tests.Fixtures;
using Xunit;

namespace UnitOfWork.Tests;

public class AsyncAdoNetTests
{
    [Fact]
    public async Task BeginAsync_Uses_OpenAsync_And_BeginTransactionAsync()
    {
        var connection = new ControlledDbConnection();
        var manager = new UnitOfWorkManager(
            new ControlledConnectionFactory(connection),
            (_, _) => throw new NotSupportedException());

        var begin = manager.BeginAsync();
        await connection.OpenAsyncStarted.Task;
        connection.ReleaseOpen();
        await using var scope = await begin;

        Assert.Equal(1, connection.OpenAsyncCount);
        Assert.Equal(0, connection.OpenCount);
        Assert.Equal(1, connection.BeginTransactionAsyncCount);
        Assert.Equal(0, connection.BeginTransactionCount);
        Assert.True(connection.LastOpenCancellationToken.CanBeCanceled);
        Assert.Equal(
            connection.LastOpenCancellationToken,
            connection.LastBeginCancellationToken);

        await scope.RollbackAsync();
    }

    [Fact]
    public async Task Outermost_Complete_Uses_CommitAsync()
    {
        var connection = new ControlledDbConnection(initiallyOpen: true);
        await using var scope = await new UnitOfWorkManager(
            new ControlledConnectionFactory(connection),
            (_, _) => throw new NotSupportedException()).BeginAsync();

        await scope.CompleteAsync();

        Assert.Equal(1, connection.LastTransaction!.CommitAsyncCount);
        Assert.Equal(0, connection.LastTransaction.CommitSyncCount);
    }

    [Fact]
    public async Task Outermost_Rollback_Uses_RollbackAsync()
    {
        var connection = new ControlledDbConnection(initiallyOpen: true);
        await using var scope = await new UnitOfWorkManager(
            new ControlledConnectionFactory(connection),
            (_, _) => throw new NotSupportedException()).BeginAsync();

        await scope.RollbackAsync();

        Assert.Equal(1, connection.LastTransaction!.RollbackAsyncCount);
        Assert.Equal(0, connection.LastTransaction.RollbackSyncCount);
    }

    [Fact]
    public async Task Open_And_Begin_Use_Async_Provider_APIs()
    {
        var provider = new AsyncOnlyDbConnection();
        var manager = CreateManager(provider);

        _ = await manager.BeginAsync();

        Assert.Equal(1, provider.OpenAsyncCount);
        Assert.Equal(1, provider.BeginTransactionAsyncCount);
    }

    [Fact]
    public async Task ExecuteNonQueryAsync_Uses_Provider_Async_API()
    {
        var provider = new AsyncOnlyDbConnection();
        var scope = await CreateManager(provider).BeginAsync();
        using var command = scope.Connection.CreateCommand();

        var result = await command.ExecuteNonQueryAsync(CancellationToken.None);

        Assert.Equal(7, result);
        Assert.Equal(1, provider.ExecuteNonQueryAsyncCount);
    }

    [Fact]
    public async Task ExecuteScalarAsync_Uses_Provider_Async_API()
    {
        var provider = new AsyncOnlyDbConnection();
        var scope = await CreateManager(provider).BeginAsync();
        using var command = scope.Connection.CreateCommand();

        var result = await command.ExecuteScalarAsync(CancellationToken.None);

        Assert.Equal("async scalar", result);
        Assert.Equal(1, provider.ExecuteScalarAsyncCount);
    }

    [Fact]
    public async Task PrepareAsync_Uses_Provider_Async_API()
    {
        var provider = new AsyncOnlyDbConnection();
        var scope = await CreateManager(provider).BeginAsync();
        using var command = scope.Connection.CreateCommand();

        await command.PrepareAsync(CancellationToken.None);

        Assert.Equal(1, provider.PrepareAsyncCount);
    }

    [Fact]
    public async Task Cancellation_Token_Is_Forwarded_To_Command()
    {
        var provider = new AsyncOnlyDbConnection();
        var scope = await CreateManager(provider).BeginAsync();
        using var command = scope.Connection.CreateCommand();
        using var cancellation = new CancellationTokenSource();

        await command.ExecuteScalarAsync(cancellation.Token);

        Assert.Equal(cancellation.Token, provider.LastCancellationToken);
    }

    [Fact]
    public async Task Canceled_Command_Releases_Operation_Lease()
    {
        var provider = new AsyncOnlyDbConnection { BlockOperations = true };
        var scope = await CreateManager(provider).BeginAsync();
        using var canceledCommand = scope.Connection.CreateCommand();
        using var sequentialCommand = scope.Connection.CreateCommand();
        using var cancellation = new CancellationTokenSource();

        var canceled = canceledCommand.ExecuteScalarAsync(cancellation.Token);
        await provider.OperationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        provider.ReleaseOperation();
        Assert.Equal("async scalar", await sequentialCommand.ExecuteScalarAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Sync_Command_Still_Uses_Provider_Sync_API()
    {
        var provider = new AsyncOnlyDbConnection();
        var scope = await CreateManager(provider).BeginAsync();
        using var command = scope.Connection.CreateCommand();

        var exception = Assert.Throws<InvalidOperationException>(command.ExecuteScalar);

        Assert.Equal("Synchronous provider API was used.", exception.Message);
    }

    [Fact]
    public async Task Overlapping_Async_Commands_Fail_Fast_Deterministically()
    {
        var provider = new AsyncOnlyDbConnection { BlockOperations = true };
        var scope = await CreateManager(provider).BeginAsync();
        using var command1 = scope.Connection.CreateCommand();
        using var command2 = scope.Connection.CreateCommand();

        var first = command1.ExecuteScalarAsync(CancellationToken.None);
        await provider.OperationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<UnitOfWorkConcurrencyException>(
            () => command2.ExecuteScalarAsync(CancellationToken.None));

        provider.ReleaseOperation();
        await first;
    }

    [Fact]
    public async Task Command_Cancel_Can_Reach_Active_Provider_Operation()
    {
        var provider = new AsyncOnlyDbConnection { BlockOperations = true };
        var scope = await CreateManager(provider).BeginAsync();
        using var command = scope.Connection.CreateCommand();

        var active = command.ExecuteScalarAsync(CancellationToken.None);
        await provider.OperationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        command.Cancel();

        Assert.Equal(1, provider.CancelCount);
        provider.ReleaseOperation();
        await active;
    }

    private static UnitOfWorkManager CreateManager(AsyncOnlyDbConnection provider) =>
        new(new ControlledConnectionFactory(provider), (_, _) => throw new NotSupportedException());
}
