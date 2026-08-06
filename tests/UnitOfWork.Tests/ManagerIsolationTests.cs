using UnitOfWork.Core;
using UnitOfWork.Tests.Fixtures;
using Xunit;

namespace UnitOfWork.Tests;

public sealed class ManagerIsolationTests
{
    private sealed class RepositoryMarker { }

    private static UnitOfWorkManager CreateManager(IDbConnectionFactory factory) =>
        new(factory, (type, _) =>
            type == typeof(RepositoryMarker)
                ? new RepositoryMarker()
                : throw new NotSupportedException());

    [Fact]
    public async Task Initialization_Cancellation_Clears_Ambient_And_Allows_Fresh_Begin()
    {
        var canceledConnection = new ControlledDbConnection();
        var freshConnection = new ControlledDbConnection(initiallyOpen: true);
        var factory = new ControlledConnectionFactory(canceledConnection, freshConnection);
        var manager = CreateManager(factory);
        using var cancellation = new CancellationTokenSource();

        var canceledBegin = manager.BeginAsync(cancellationToken: cancellation.Token);
        await canceledConnection.OpenAsyncStarted.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledBegin);
        Assert.True(canceledConnection.LastOpenCancellationToken.IsCancellationRequested);
        Assert.NotEqual(cancellation.Token, canceledConnection.LastOpenCancellationToken);
        Assert.Equal(1, canceledConnection.ConnectionDisposeAsyncCount);
        Assert.False(manager.HasCurrent);

        await using var freshScope = await manager.BeginAsync();
        Assert.Equal(2, factory.CreateCount);
        Assert.Same(freshScope.Connection, manager.Current.Connection);
        await freshScope.RollbackAsync();
    }

    [Fact]
    public async Task Canceled_Nested_Wait_Releases_Only_Its_Scope_Reservation()
    {
        var connection = new ControlledDbConnection();
        var manager = CreateManager(new ControlledConnectionFactory(connection));
        using var nestedCancellation = new CancellationTokenSource();

        var outerBegin = manager.BeginAsync();
        await connection.OpenAsyncStarted.Task;
        var nestedBegin = manager.BeginAsync(cancellationToken: nestedCancellation.Token);
        nestedCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => nestedBegin);
        Assert.False(connection.LastOpenCancellationToken.IsCancellationRequested);
        Assert.Equal(0, connection.ConnectionDisposeAsyncCount);
        Assert.True(manager.HasCurrent);

        connection.ReleaseOpen();
        await using var outer = await outerBegin;
        await outer.CompleteAsync();

        Assert.Equal(1, connection.LastTransaction!.CommitAsyncCount);
        Assert.Equal(0, connection.LastTransaction.RollbackAsyncCount);
    }

    [Fact]
    public async Task Last_Canceled_Initialization_Wait_Cancels_Root_Initialization()
    {
        var connection = new ControlledDbConnection(
            initiallyOpen: true,
            blockBeginTransactionAsync: true);
        var manager = CreateManager(new ControlledConnectionFactory(connection));
        using var firstCancellation = new CancellationTokenSource();
        using var lastCancellation = new CancellationTokenSource();

        var firstBegin = manager.BeginAsync(cancellationToken: firstCancellation.Token);
        await connection.BeginTransactionAsyncStarted.Task;
        var lastBegin = manager.BeginAsync(cancellationToken: lastCancellation.Token);

        firstCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstBegin);
        Assert.False(connection.LastBeginCancellationToken.IsCancellationRequested);
        Assert.Equal(0, connection.ConnectionDisposeAsyncCount);

        lastCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lastBegin);

        Assert.True(connection.LastBeginCancellationToken.IsCancellationRequested);
        Assert.NotEqual(lastCancellation.Token, connection.LastBeginCancellationToken);
        Assert.Equal(1, connection.ConnectionDisposeAsyncCount);
        Assert.False(manager.HasCurrent);
    }

    [Fact]
    public async Task Last_Canceled_Wait_When_Begin_Ignores_Cancellation_Cleans_Root()
    {
        var canceledConnection = new ControlledDbConnection(
            initiallyOpen: true,
            blockBeginTransactionAsync: true,
            ignoreBeginTransactionAsyncCancellation: true);
        var freshConnection = new ControlledDbConnection(initiallyOpen: true);
        var factory = new ControlledConnectionFactory(canceledConnection, freshConnection);
        var manager = CreateManager(factory);
        using var cancellation = new CancellationTokenSource();

        var canceledBegin = manager.BeginAsync(cancellationToken: cancellation.Token);
        await canceledConnection.BeginTransactionAsyncStarted.Task;
        var initializationToken = canceledConnection.LastBeginCancellationToken;
        _ = initializationToken.WaitHandle;
        cancellation.Cancel();
        await canceledConnection.BeginTransactionAsyncCancellationObserved.Task;
        canceledConnection.ReleaseBeginTransactionAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledBegin);
        Assert.NotNull(canceledConnection.LastTransaction);
        Assert.Equal(1, canceledConnection.TransactionDisposeAsyncCount);
        Assert.Equal(1, canceledConnection.ConnectionDisposeAsyncCount);
        Assert.False(manager.HasCurrent);
        Assert.Throws<ObjectDisposedException>(() => initializationToken.WaitHandle);

        await using var freshScope = await manager.BeginAsync();
        Assert.Equal(2, factory.CreateCount);
        Assert.Same(freshScope.Connection, manager.Current.Connection);
        await freshScope.RollbackAsync();
    }

    [Fact]
    public async Task Every_Begin_Returns_A_Distinct_Scope_Over_One_Current_Context()
    {
        var connection = new ControlledDbConnection(initiallyOpen: true);
        var factory = new ControlledConnectionFactory(connection);
        var manager = CreateManager(factory);

        using var outer = await manager.BeginAsync();
        var current = manager.Current;
        using var inner = await manager.BeginAsync();

        Assert.NotSame(outer, inner);
        Assert.Same(current, manager.Current);
        Assert.Same(current.Connection, outer.Connection);
        Assert.Same(current.Connection, inner.Connection);
        Assert.Equal(1, factory.CreateCount);

        await inner.CompleteAsync();
        await outer.CompleteAsync();
    }

    [Fact]
    public async Task Nested_Begin_During_Initialization_Returns_Distinct_Scopes_And_Awaits_One_Root()
    {
        var connection = new ControlledDbConnection();
        var factory = new ControlledConnectionFactory(connection);
        var manager = CreateManager(factory);

        var firstBegin = manager.BeginAsync();
        var nestedBegin = manager.BeginAsync();

        Assert.Equal(1, factory.CreateCount);
        Assert.False(firstBegin.IsCompleted);
        Assert.False(nestedBegin.IsCompleted);

        connection.ReleaseOpen();
        using var outer = await firstBegin;
        using var inner = await nestedBegin;

        Assert.NotSame(outer, inner);
        Assert.Same(outer.Connection, inner.Connection);

        await inner.CompleteAsync();
        await outer.CompleteAsync();
    }

    [Fact]
    public async Task Reentrant_Begin_During_OpenAsync_Awaits_The_Same_Initialization()
    {
        var connection = new ControlledDbConnection();
        var factory = new ControlledConnectionFactory(connection);
        var manager = CreateManager(factory);
        Task<IUnitOfWorkScope>? reentrantBegin = null;
        connection.Opening = () => reentrantBegin = manager.BeginAsync();

        var firstBegin = manager.BeginAsync();

        Assert.NotNull(reentrantBegin);
        Assert.False(firstBegin.IsCompleted);
        Assert.False(reentrantBegin!.IsCompleted);
        Assert.Equal(1, factory.CreateCount);

        connection.ReleaseOpen();
        using var outer = await firstBegin;
        using var inner = await reentrantBegin;

        Assert.NotSame(outer, inner);
        Assert.Same(outer.Connection, inner.Connection);

        await inner.CompleteAsync();
        await outer.CompleteAsync();
    }

    [Fact]
    public async Task Two_Manager_Instances_Do_Not_Share_Ambient_Root()
    {
        var firstConnection = new ControlledDbConnection(initiallyOpen: true);
        var secondConnection = new ControlledDbConnection(initiallyOpen: true);
        var firstFactory = new ControlledConnectionFactory(firstConnection);
        var secondFactory = new ControlledConnectionFactory(secondConnection);
        var firstManager = CreateManager(firstFactory);
        var secondManager = CreateManager(secondFactory);

        using var firstScope = await firstManager.BeginAsync();
        using var secondScope = await secondManager.BeginAsync();

        Assert.NotSame(firstScope.Connection, secondScope.Connection);
        Assert.Same(firstScope.Connection, firstManager.Current.Connection);
        Assert.Same(secondScope.Connection, secondManager.Current.Connection);
        Assert.Equal(1, firstFactory.CreateCount);
        Assert.Equal(1, secondFactory.CreateCount);

        await firstScope.CompleteAsync();
        Assert.False(firstManager.HasCurrent);
        Assert.True(secondManager.HasCurrent);

        await secondScope.CompleteAsync();
    }

    [Fact]
    public async Task Outermost_Finalization_Clears_Current_Automatically()
    {
        var connection = new ControlledDbConnection(initiallyOpen: true);
        var manager = CreateManager(new ControlledConnectionFactory(connection));

        using var scope = await manager.BeginAsync();
        Assert.True(manager.HasCurrent);

        await scope.CompleteAsync();

        Assert.False(manager.HasCurrent);
        Assert.Throws<InvalidOperationException>(() => manager.Current);
    }

    [Fact]
    public async Task Initialization_Failure_Clears_Current_And_Allows_Fresh_Begin()
    {
        var initializationFailure = new InvalidOperationException("transaction initialization failed");
        var failingConnection = new ControlledDbConnection(
            initiallyOpen: true,
            beginTransactionException: initializationFailure);
        var succeedingConnection = new ControlledDbConnection(initiallyOpen: true);
        var factory = new ControlledConnectionFactory(failingConnection, succeedingConnection);
        var manager = CreateManager(factory);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.BeginAsync());

        Assert.Same(initializationFailure, actual);
        Assert.True(failingConnection.IsDisposed);
        Assert.False(manager.HasCurrent);
        Assert.Throws<InvalidOperationException>(() => manager.Current);

        using var freshScope = await manager.BeginAsync();
        Assert.NotSame(succeedingConnection, freshScope.Connection);
        Assert.Same(freshScope.Connection, manager.Current.Connection);
        Assert.Equal(2, factory.CreateCount);

        await freshScope.CompleteAsync();
    }

    [Fact]
    public async Task Resource_Cleanup_Failure_Clears_Current_And_Allows_Fresh_Begin()
    {
        var cleanupFailure = new IOException("transaction cleanup failed");
        var failingConnection = new ControlledDbConnection(
            initiallyOpen: true,
            transactionDisposeException: cleanupFailure);
        var succeedingConnection = new ControlledDbConnection(initiallyOpen: true);
        var factory = new ControlledConnectionFactory(failingConnection, succeedingConnection);
        var manager = CreateManager(factory);

        using var failingScope = await manager.BeginAsync();

        var actual = await Assert.ThrowsAsync<IOException>(() => failingScope.CompleteAsync());

        Assert.Same(cleanupFailure, actual);
        Assert.True(failingConnection.IsDisposed);
        Assert.False(manager.HasCurrent);
        Assert.Throws<InvalidOperationException>(() => manager.Current);

        using var freshScope = await manager.BeginAsync();
        Assert.Equal(2, factory.CreateCount);
        Assert.Same(freshScope.Connection, manager.Current.Connection);

        await freshScope.RollbackAsync();
    }
}
