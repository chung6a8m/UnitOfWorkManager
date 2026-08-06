using UnitOfWork.Core;
using UnitOfWork.Tests.Fixtures;
using Xunit;

namespace UnitOfWork.Tests;

public class DisposalTests
{
    private static UnitOfWorkManager CreateManager(IDbConnectionFactory factory) =>
        new(factory, (_, _) => throw new NotSupportedException());

    [Fact]
    public async Task DisposeAsync_Without_Completion_Explicitly_Rolls_Back()
    {
        var connection = new ControlledDbConnection(initiallyOpen: true);
        var manager = CreateManager(new ControlledConnectionFactory(connection));
        var scope = await manager.BeginAsync();

        await scope.DisposeAsync();

        Assert.Equal(1, connection.LastTransaction!.RollbackAsyncCount);
        Assert.Equal(0, connection.LastTransaction.RollbackSyncCount);
        Assert.Equal(CancellationToken.None, connection.LastRollbackCancellationToken);
        Assert.False(manager.HasCurrent);
    }

    [Fact]
    public async Task DisposeAsync_Attempts_Transaction_And_Connection_Async_Disposal()
    {
        var transactionFailure = new IOException("transaction async dispose failed");
        var connectionFailure = new IOException("connection async dispose failed");
        var connection = new ControlledDbConnection(
            initiallyOpen: true,
            transactionDisposeAsyncException: transactionFailure,
            connectionDisposeAsyncException: connectionFailure);
        var scope = await CreateManager(new ControlledConnectionFactory(connection)).BeginAsync();

        var actual = await Assert.ThrowsAsync<AggregateException>(
            async () => await scope.DisposeAsync());

        Assert.Collection(
            actual.InnerExceptions,
            error => Assert.Same(transactionFailure, error),
            error => Assert.Same(connectionFailure, error));
        Assert.Equal(1, connection.LastTransaction!.TransactionDisposeAsyncCount);
        Assert.Equal(0, connection.LastTransaction.DisposeSyncCount);
        Assert.Equal(1, connection.ConnectionDisposeAsyncCount);
        Assert.Equal(0, connection.ConnectionDisposeCount);
    }

    [Fact]
    public async Task Nested_DisposeAsync_Requests_Rollback_Without_Disposing_Root()
    {
        var connection = new ControlledDbConnection(initiallyOpen: true);
        var manager = CreateManager(new ControlledConnectionFactory(connection));
        await using var outer = await manager.BeginAsync();
        var inner = await manager.BeginAsync();

        await inner.DisposeAsync();

        Assert.True(manager.HasCurrent);
        Assert.Equal(0, connection.LastTransaction!.RollbackAsyncCount);
        Assert.Equal(0, connection.ConnectionDisposeAsyncCount);

        await outer.CompleteAsync();

        Assert.Equal(1, connection.LastTransaction.RollbackAsyncCount);
        Assert.Equal(1, connection.ConnectionDisposeAsyncCount);
    }

    [Fact]
    public async Task Completed_Scope_Dispose_Is_Idempotent()
    {
        var connection = new ControlledDbConnection(initiallyOpen: true);
        var manager = CreateManager(new ControlledConnectionFactory(connection));
        var scope = await manager.BeginAsync();

        await scope.CompleteAsync();
        var firstDispose = Record.Exception(scope.Dispose);
        var secondDispose = Record.Exception(scope.Dispose);

        Assert.Null(firstDispose);
        Assert.Null(secondDispose);
        Assert.False(manager.HasCurrent);
        Assert.Equal(1, connection.LastTransaction!.CommitCount);
        Assert.Equal(0, connection.LastTransaction.RollbackCount);
        Assert.True(connection.IsDisposed);
    }

    [Fact]
    public async Task Rollback_Completed_Scope_Dispose_Is_Idempotent()
    {
        var connection = new ControlledDbConnection(initiallyOpen: true);
        var manager = CreateManager(new ControlledConnectionFactory(connection));
        var scope = await manager.BeginAsync();

        await scope.RollbackAsync();
        var firstDispose = Record.Exception(scope.Dispose);
        var secondDispose = Record.Exception(scope.Dispose);

        Assert.Null(firstDispose);
        Assert.Null(secondDispose);
        Assert.False(manager.HasCurrent);
        Assert.Equal(0, connection.LastTransaction!.CommitCount);
        Assert.Equal(1, connection.LastTransaction.RollbackCount);
        Assert.True(connection.IsDisposed);
    }

    [Fact]
    public async Task Incomplete_Outermost_Scope_Dispose_Rolls_Back_And_Clears_Current()
    {
        var connection = new ControlledDbConnection(initiallyOpen: true);
        var manager = CreateManager(new ControlledConnectionFactory(connection));
        var scope = await manager.BeginAsync();

        scope.Dispose();

        Assert.False(manager.HasCurrent);
        Assert.Throws<InvalidOperationException>(() => manager.Current);
        Assert.Equal(0, connection.LastTransaction!.CommitCount);
        Assert.Equal(1, connection.LastTransaction.RollbackCount);
        Assert.True(connection.IsDisposed);
    }

    [Fact]
    public async Task Incomplete_Inner_Scope_Dispose_Does_Not_Dispose_Root()
    {
        var connection = new ControlledDbConnection(initiallyOpen: true);
        var manager = CreateManager(new ControlledConnectionFactory(connection));
        var outer = await manager.BeginAsync();
        var inner = await manager.BeginAsync();

        inner.Dispose();

        Assert.True(manager.HasCurrent);
        Assert.False(connection.IsDisposed);
        Assert.Equal(0, connection.LastTransaction!.CommitCount);
        Assert.Equal(0, connection.LastTransaction.RollbackCount);

        await outer.CompleteAsync();
        outer.Dispose();
        inner.Dispose();

        Assert.False(manager.HasCurrent);
        Assert.Equal(0, connection.LastTransaction.CommitCount);
        Assert.Equal(1, connection.LastTransaction.RollbackCount);
        Assert.True(connection.IsDisposed);
    }

    [Fact]
    public async Task Fresh_Begin_Works_After_Root_Finalization_Without_ClearCurrent()
    {
        var firstConnection = new ControlledDbConnection(initiallyOpen: true);
        var secondConnection = new ControlledDbConnection(initiallyOpen: true);
        var factory = new ControlledConnectionFactory(firstConnection, secondConnection);
        var manager = CreateManager(factory);
        var first = await manager.BeginAsync();
        var firstFacade = first.Connection;

        await first.CompleteAsync();
        first.Dispose();
        var second = await manager.BeginAsync();

        Assert.Equal(2, factory.CreateCount);
        Assert.NotSame(firstFacade, second.Connection);
        Assert.True(manager.HasCurrent);

        await second.RollbackAsync();
        second.Dispose();
        Assert.False(manager.HasCurrent);
    }

    [Fact]
    public async Task Commit_Failure_Clears_Manager_Current_And_Next_Begin_Is_Fresh()
    {
        var commitFailure = new InvalidOperationException("commit failed");
        var failedConnection = new ControlledDbConnection(
            initiallyOpen: true,
            commitException: commitFailure);
        var freshConnection = new ControlledDbConnection(initiallyOpen: true);
        var factory = new ControlledConnectionFactory(failedConnection, freshConnection);
        var manager = CreateManager(factory);
        var first = await manager.BeginAsync();

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => first.CompleteAsync());

        Assert.Same(commitFailure, actual);
        Assert.False(manager.HasCurrent);
        Assert.Throws<InvalidOperationException>(() => manager.Current);

        using var second = await manager.BeginAsync();

        Assert.Equal(2, factory.CreateCount);
        Assert.Same(second.Connection, manager.Current.Connection);
        Assert.False(freshConnection.IsDisposed);
        await second.RollbackAsync();
    }
}
