using System.Data;
using System.Data.Common;
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
    public async Task Dispose_Racing_With_Completed_Settlement_Remains_Idempotent()
    {
        var connection = new ControlledDbConnection(initiallyOpen: true);
        var ownershipCheckStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOwnershipCheck = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var ownershipCheckCount = 0;
        var root = new RootUnitOfWork(
            connection,
            (_, _) => throw new NotSupportedException(),
            () =>
            {
                if (Interlocked.Increment(ref ownershipCheckCount) == 1)
                {
                    ownershipCheckStarted.TrySetResult();
                    releaseOwnershipCheck.Task.GetAwaiter().GetResult();
                }

                return true;
            },
            () => { });
        var scope = root.AcquireScope();
        await root.InitializeAsync();
        Task<Exception>? concurrentDispose = null;

        try
        {
            concurrentDispose = Task.Run(() => Record.Exception(scope.Dispose));
            await ownershipCheckStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await scope.CompleteAsync();
        }
        finally
        {
            releaseOwnershipCheck.TrySetResult();
        }

        Assert.Null(await concurrentDispose!);
        Assert.Equal(UnitOfWorkCompletionOutcome.Committed, root.CompletionOutcome);
        Assert.Equal(1, connection.LastTransaction!.CommitCount);
        Assert.Equal(0, connection.LastTransaction.RollbackCount);
        Assert.Equal(1, connection.LastTransaction.DisposeCount);
    }

    [Fact]
    public async Task Foreign_Dispose_During_Rejected_Settlement_Is_Rejected_After_State_Restores_To_Active()
    {
        var connection = new ControlledDbConnection(initiallyOpen: true);
        var ownsRoot = new AsyncLocal<bool> { Value = true };
        var holdRootLock = new AsyncLocal<bool>();
        var rootLockHeld = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRootLock = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var root = new RootUnitOfWork(
            connection,
            (_, _) => throw new NotSupportedException(),
            () =>
            {
                if (holdRootLock.Value)
                {
                    rootLockHeld.TrySetResult();
                    releaseRootLock.Task.GetAwaiter().GetResult();
                }

                return ownsRoot.Value;
            },
            () => { });
        var scope = root.AcquireScope();
        await root.InitializeAsync();
        var operationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOperation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var ownerSettlementStarted = new ManualResetEventSlim();
        using var foreignDisposeStarted = new ManualResetEventSlim();
        using var foreignDisposeFinished = new ManualResetEventSlim();
        Exception? ownerSettlementException = null;
        Exception? foreignDisposeException = null;
        Exception? lifecycleLockHolderException = null;
        var activeOperation = root.RunGuardedAsync(async () =>
        {
            operationStarted.TrySetResult();
            await releaseOperation.Task;
            return true;
        });
        Task? lifecycleLockHolder = null;
        Thread? ownerSettlementThread = null;
        Thread? foreignDisposeThread = null;
        var ownerThreadJoined = true;
        var foreignThreadJoined = true;

        try
        {
            await operationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            lifecycleLockHolder = Task.Run(async () =>
            {
                ownsRoot.Value = true;
                holdRootLock.Value = true;
                lifecycleLockHolderException = await Record.ExceptionAsync(
                    () => root.RunGuardedAsync(() => Task.FromResult(true)));
            });
            await rootLockHeld.Task.WaitAsync(TimeSpan.FromSeconds(5));

            ownerSettlementThread = new Thread(() =>
            {
                ownsRoot.Value = true;
                ownerSettlementStarted.Set();

                try
                {
                    scope.CompleteAsync().GetAwaiter().GetResult();
                }
                catch (Exception exception)
                {
                    ownerSettlementException = exception;
                }
            })
            {
                IsBackground = true,
                Name = "owner-scope-settlement"
            };
            ownerSettlementThread.Start();

            Assert.True(ownerSettlementStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(
                SpinWait.SpinUntil(
                    () => IsWaiting(ownerSettlementThread),
                    TimeSpan.FromSeconds(5)),
                "Owner settlement did not block at the root lifecycle gate.");

            foreignDisposeThread = new Thread(() =>
            {
                ownsRoot.Value = false;
                foreignDisposeStarted.Set();

                try
                {
                    scope.Dispose();
                }
                catch (Exception exception)
                {
                    foreignDisposeException = exception;
                }
                finally
                {
                    foreignDisposeFinished.Set();
                }
            })
            {
                IsBackground = true,
                Name = "foreign-scope-dispose"
            };
            foreignDisposeThread.Start();

            Assert.True(foreignDisposeStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(
                SpinWait.SpinUntil(
                    () => foreignDisposeFinished.IsSet || IsWaiting(foreignDisposeThread),
                    TimeSpan.FromSeconds(5)),
                "Foreign dispose neither returned nor waited for the settlement decision.");
        }
        finally
        {
            releaseRootLock.TrySetResult();

            try
            {
                if (lifecycleLockHolder is not null)
                    await lifecycleLockHolder.WaitAsync(TimeSpan.FromSeconds(5));

                if (ownerSettlementThread is not null)
                    ownerThreadJoined = ownerSettlementThread.Join(TimeSpan.FromSeconds(5));

                if (foreignDisposeThread is not null)
                    foreignThreadJoined = foreignDisposeThread.Join(TimeSpan.FromSeconds(5));
            }
            finally
            {
                releaseOperation.TrySetResult();
                await activeOperation.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        Assert.True(ownerThreadJoined, "Owner settlement thread did not exit.");
        Assert.True(foreignThreadJoined, "Foreign dispose thread did not exit.");
        Assert.IsType<UnitOfWorkConcurrencyException>(lifecycleLockHolderException);
        Assert.IsType<UnitOfWorkConcurrencyException>(ownerSettlementException);
        Assert.IsType<UnitOfWorkConcurrencyException>(foreignDisposeException);
        Assert.Equal(1, root.ActiveScopeCount);
        Assert.Equal(UnitOfWorkLifecycleState.Active, root.LifecycleState);
        Assert.Equal(UnitOfWorkCompletionOutcome.None, root.CompletionOutcome);
        Assert.False(root.RollbackRequested);
        Assert.False(connection.IsDisposed);
        Assert.Equal(0, connection.LastTransaction!.CommitCount);
        Assert.Equal(0, connection.LastTransaction.RollbackCount);
        Assert.Equal(0, connection.LastTransaction.DisposeCount);

        await scope.CompleteAsync();

        Assert.Equal(0, root.ActiveScopeCount);
        Assert.Equal(UnitOfWorkCompletionOutcome.Committed, root.CompletionOutcome);
        Assert.Equal(1, connection.LastTransaction.CommitCount);
        Assert.Equal(0, connection.LastTransaction.RollbackCount);
        Assert.Equal(1, connection.LastTransaction.DisposeCount);
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

    [Fact]
    public async Task Commit_Failure_Faults_Root_And_Is_Not_Retried()
    {
        var commitFailure = new InvalidOperationException("commit failed");
        var connection = new ControlledDbConnection(
            initiallyOpen: true,
            commitException: commitFailure);
        var root = CreateRoot(connection);
        var scope = root.AcquireScope();
        await root.InitializeAsync();

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => scope.CompleteAsync());

        Assert.Same(commitFailure, actual);
        Assert.Equal(1, connection.LastTransaction!.CommitCount);
        Assert.Equal(UnitOfWorkLifecycleState.Faulted, root.LifecycleState);
        Assert.Equal(UnitOfWorkCompletionOutcome.Faulted, root.CompletionOutcome);
        Assert.Equal(0, root.ActiveScopeCount);
        Assert.True(connection.IsDisposed);

        await Assert.ThrowsAsync<UnitOfWorkStateException>(() => scope.CompleteAsync());
        Assert.Equal(1, connection.LastTransaction.CommitCount);
    }

    [Fact]
    public async Task Rollback_Failure_Faults_Root_And_Is_Not_Retried()
    {
        var rollbackFailure = new InvalidOperationException("rollback failed");
        var connection = new ControlledDbConnection(
            initiallyOpen: true,
            rollbackException: rollbackFailure);
        var root = CreateRoot(connection);
        var scope = root.AcquireScope();
        await root.InitializeAsync();

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => scope.RollbackAsync());

        Assert.Same(rollbackFailure, actual);
        Assert.Equal(1, connection.LastTransaction!.RollbackCount);
        Assert.Equal(UnitOfWorkLifecycleState.Faulted, root.LifecycleState);
        Assert.Equal(UnitOfWorkCompletionOutcome.Faulted, root.CompletionOutcome);
        Assert.Equal(0, root.ActiveScopeCount);
        Assert.True(connection.IsDisposed);

        await Assert.ThrowsAsync<UnitOfWorkStateException>(() => scope.RollbackAsync());
        Assert.Equal(1, connection.LastTransaction.RollbackCount);
    }

    [Fact]
    public async Task Cleanup_Attempts_Connection_When_Transaction_Disposal_Fails()
    {
        var transactionDisposeFailure = new InvalidOperationException("transaction dispose failed");
        var connection = new ControlledDbConnection(
            initiallyOpen: true,
            transactionDisposeException: transactionDisposeFailure);
        var finishedCount = 0;
        var root = CreateRoot(connection, () => finishedCount++);
        var scope = root.AcquireScope();
        await root.InitializeAsync();

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => scope.CompleteAsync());

        Assert.Same(transactionDisposeFailure, actual);
        Assert.Equal(1, connection.LastTransaction!.DisposeCount);
        Assert.True(connection.IsDisposed);
        Assert.Equal(1, finishedCount);
        Assert.Equal(0, root.ActiveScopeCount);

        await Assert.ThrowsAsync<UnitOfWorkStateException>(() => scope.CompleteAsync());
        Assert.Equal(1, connection.LastTransaction.DisposeCount);
    }

    private static RootUnitOfWork CreateRoot(
        DbConnection connection,
        Action? onRootFinished = null) =>
        new(
            connection,
            (_, _) => throw new NotSupportedException("No repository is needed by lifecycle tests."),
            () => true,
            onRootFinished ?? (() => { }));

    private static bool IsWaiting(Thread thread) =>
        (thread.ThreadState & ThreadState.WaitSleepJoin) != 0;
}
