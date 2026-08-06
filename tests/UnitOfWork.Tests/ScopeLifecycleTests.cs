using System.Data;
using UnitOfWork.Core;
using UnitOfWork.Core.Exceptions;
using UnitOfWork.Tests.Fixtures;
using Xunit;

namespace UnitOfWork.Tests;

public class ScopeLifecycleTests
{
    [Fact]
    public async Task AcquireScope_Returns_Distinct_Leases_Over_One_Root()
    {
        var connection = new ControlledDbConnection(initiallyOpen: true);
        var root = CreateRoot(connection);

        var outer = root.AcquireScope();
        var inner = root.AcquireScope();
        await root.InitializeAsync();

        Assert.NotSame(outer, inner);
        Assert.Equal(2, root.ActiveScopeCount);

        await inner.CompleteAsync();
        await outer.CompleteAsync();
    }

    [Fact]
    public async Task Dispose_Incomplete_Inner_Scope_Requests_Rollback_Without_Disposing_Root()
    {
        var connection = new ControlledDbConnection(initiallyOpen: true);
        var root = CreateRoot(connection);
        var outer = root.AcquireScope();
        var inner = root.AcquireScope();
        await root.InitializeAsync();

        inner.Dispose();

        Assert.True(root.RollbackRequested);
        Assert.Equal(1, root.ActiveScopeCount);
        Assert.False(connection.IsDisposed);

        await outer.CompleteAsync();
        Assert.Equal(UnitOfWorkCompletionOutcome.RolledBack, root.CompletionOutcome);
        Assert.True(connection.IsDisposed);
    }

    [Fact]
    public async Task Scope_Cannot_Change_Outcome_After_Completion()
    {
        var connection = new ControlledDbConnection(initiallyOpen: true);
        var root = CreateRoot(connection);
        var outer = root.AcquireScope();
        var inner = root.AcquireScope();
        await root.InitializeAsync();

        await inner.CompleteAsync();

        await Assert.ThrowsAsync<UnitOfWorkStateException>(() => inner.RollbackAsync());
        Assert.False(root.RollbackRequested);
        Assert.Equal(1, root.ActiveScopeCount);

        await outer.CompleteAsync();
        Assert.Equal(UnitOfWorkCompletionOutcome.Committed, root.CompletionOutcome);
    }

    [Fact]
    public async Task Double_Complete_Throws_And_Finalizes_Exactly_Once()
    {
        var connection = new ControlledDbConnection(initiallyOpen: true);
        var finalizationCount = 0;
        var root = CreateRoot(connection, () => finalizationCount++);
        var scope = root.AcquireScope();
        await root.InitializeAsync();

        await scope.CompleteAsync();

        await Assert.ThrowsAsync<UnitOfWorkStateException>(() => scope.CompleteAsync());
        Assert.Equal(1, finalizationCount);
        Assert.Equal(UnitOfWorkCompletionOutcome.Committed, root.CompletionOutcome);
        Assert.True(connection.IsDisposed);
    }

    [Fact]
    public async Task Concurrent_InitializeAsync_Uses_One_Open_And_Transaction()
    {
        var connection = new ControlledDbConnection();
        var openingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var openCount = 0;
        connection.Opening = () =>
        {
            Interlocked.Increment(ref openCount);
            openingStarted.TrySetResult();
        };
        var root = CreateRoot(connection);

        var firstInitialization = root.InitializeAsync();
        await openingStarted.Task;
        var secondInitialization = root.InitializeAsync();

        await Task.Yield();
        Assert.Equal(1, Volatile.Read(ref openCount));

        connection.ReleaseOpen();
        await Task.WhenAll(firstInitialization, secondInitialization);
    }

    [Fact]
    public async Task Initialization_Failure_Runs_Finish_Callback_When_Cleanup_Throws()
    {
        var initializationFailure = new InvalidOperationException("begin failed");
        var cleanupFailure = new InvalidOperationException("dispose failed");
        var connection = new ControlledDbConnection(
            initiallyOpen: true,
            beginTransactionException: initializationFailure,
            disposeException: cleanupFailure);
        var finishedCount = 0;
        var root = CreateRoot(connection, () => finishedCount++);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => root.InitializeAsync());

        Assert.Same(initializationFailure, exception);
        Assert.Same(cleanupFailure, exception.Data["UnitOfWorkCleanupException"]);
        Assert.Equal(1, finishedCount);
    }

    [Fact]
    public async Task AcquireScope_Is_Rejected_After_Finalization_Has_Completed()
    {
        var connection = new ControlledDbConnection(initiallyOpen: true);
        var root = CreateRoot(connection);
        var scope = root.AcquireScope();
        await root.InitializeAsync();

        await scope.CompleteAsync();

        Assert.Throws<UnitOfWorkStateException>(() => root.AcquireScope());
        Assert.Equal(0, root.ActiveScopeCount);
    }

    private static RootUnitOfWork CreateRoot(
        IDbConnection connection,
        Action? onRootFinished = null) =>
        new(
            connection,
            (_, _) => throw new NotSupportedException("No repository is needed by lifecycle tests."),
            () => true,
            onRootFinished ?? (() => { }));
}
