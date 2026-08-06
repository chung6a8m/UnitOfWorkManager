using System.Data;
using UnitOfWork.Core;
using UnitOfWork.Core.Exceptions;
using UnitOfWork.Tests.Fixtures;
using Xunit;

namespace UnitOfWork.Tests;

public class ConcurrencyGuardTests
{
    [Fact]
    public async Task Lifecycle_Finalization_While_Operation_Is_Active_Is_Rejected_Without_Settling_Scope()
    {
        var connection = new ControlledDbConnection(initiallyOpen: true);
        var root = new RootUnitOfWork(
            connection,
            (_, _) => throw new NotSupportedException(),
            () => true,
            () => { });
        var scope = root.AcquireScope();
        await root.InitializeAsync();
        var operationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOperation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeOperation = root.RunGuardedAsync(async () =>
        {
            operationStarted.TrySetResult();
            await releaseOperation.Task;
            return true;
        });

        try
        {
            await operationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<UnitOfWorkConcurrencyException>(() => scope.CompleteAsync());

            Assert.Equal(1, root.ActiveScopeCount);
            Assert.Equal(UnitOfWorkLifecycleState.Active, root.LifecycleState);
            Assert.Equal(UnitOfWorkCompletionOutcome.None, root.CompletionOutcome);
            Assert.False(connection.IsDisposed);
            Assert.Equal(0, connection.LastTransaction!.CommitCount);
        }
        finally
        {
            releaseOperation.TrySetResult();
            await activeOperation;
        }

        await scope.CompleteAsync();

        Assert.Equal(0, root.ActiveScopeCount);
        Assert.Equal(1, connection.LastTransaction.CommitCount);
    }

    [Fact]
    public async Task Dispose_While_Operation_Is_Active_Is_Rejected_Without_Abandoning_Scope()
    {
        var connection = new ControlledDbConnection(initiallyOpen: true);
        var root = new RootUnitOfWork(
            connection,
            (_, _) => throw new NotSupportedException(),
            () => true,
            () => { });
        var scope = root.AcquireScope();
        await root.InitializeAsync();
        var operationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOperation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeOperation = root.RunGuardedAsync(async () =>
        {
            operationStarted.TrySetResult();
            await releaseOperation.Task;
            return true;
        });

        try
        {
            await operationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Throws<UnitOfWorkConcurrencyException>(scope.Dispose);

            Assert.Equal(1, root.ActiveScopeCount);
            Assert.Equal(UnitOfWorkLifecycleState.Active, root.LifecycleState);
            Assert.False(root.RollbackRequested);
        }
        finally
        {
            releaseOperation.TrySetResult();
            await activeOperation;
        }

        scope.Dispose();

        Assert.Equal(0, root.ActiveScopeCount);
        Assert.Equal(UnitOfWorkCompletionOutcome.RolledBack, root.CompletionOutcome);
        Assert.Equal(1, connection.LastTransaction!.RollbackCount);
    }

    [Fact]
    public async Task Command_Guard_Fails_Fast_Then_Releases_For_Sequential_Command()
    {
        var operationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOperation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var executionCount = 0;
        var connection = new ControlledDbConnection(
            initiallyOpen: true,
            commandFactory: innerConnection => new ControlledDbCommand(innerConnection, () =>
            {
                var execution = Interlocked.Increment(ref executionCount);
                if (execution != 1)
                    return (long)execution;

                operationStarted.TrySetResult();
                releaseOperation.Task.GetAwaiter().GetResult();
                return 1L;
            }));
        var manager = new UnitOfWorkManager(
            new ControlledConnectionFactory(connection),
            (_, _) => throw new NotSupportedException());
        using var scope = await manager.BeginAsync();
        using var firstCommand = scope.Connection.CreateCommand();
        firstCommand.CommandText = "SELECT hold_operation();";
        Task<object?>? firstOperation = null;

        try
        {
            firstOperation = Task.Run(firstCommand.ExecuteScalar);
            await operationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            using var overlappingCommand = scope.Connection.CreateCommand();
            overlappingCommand.CommandText = "SELECT 2;";
            var exception = Assert.Throws<UnitOfWorkConcurrencyException>(
                overlappingCommand.ExecuteScalar);

            Assert.Contains("already executing another operation", exception.Message);
        }
        finally
        {
            releaseOperation.TrySetResult();
            if (firstOperation is not null)
                Assert.Equal(1L, await firstOperation);
        }

        using var sequentialCommand = scope.Connection.CreateCommand();
        sequentialCommand.CommandText = "SELECT 42;";
        Assert.Equal(2L, sequentialCommand.ExecuteScalar());

        await scope.RollbackAsync();
    }

    [Fact]
    public async Task Prepare_Guard_Fails_Fast_Then_Releases_For_Sequential_Prepare()
    {
        var operationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOperation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var prepareCount = 0;
        var connection = new ControlledDbConnection(
            initiallyOpen: true,
            commandFactory: innerConnection => new ControlledDbCommand(
                innerConnection,
                () =>
                {
                    operationStarted.TrySetResult();
                    releaseOperation.Task.GetAwaiter().GetResult();
                    return 1L;
                },
                () => Interlocked.Increment(ref prepareCount)));
        var manager = new UnitOfWorkManager(
            new ControlledConnectionFactory(connection),
            (_, _) => throw new NotSupportedException());
        using var scope = await manager.BeginAsync();
        using var activeCommand = scope.Connection.CreateCommand();
        using var prepareCommand = scope.Connection.CreateCommand();
        Task<object?>? activeOperation = null;

        try
        {
            activeOperation = Task.Run(activeCommand.ExecuteScalar);
            await operationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var exception = Assert.Throws<UnitOfWorkConcurrencyException>(prepareCommand.Prepare);

            Assert.Contains("already executing another operation", exception.Message);
            Assert.Equal(0, Volatile.Read(ref prepareCount));
        }
        finally
        {
            releaseOperation.TrySetResult();
            if (activeOperation is not null)
                Assert.Equal(1L, await activeOperation);
        }

        prepareCommand.Prepare();

        Assert.Equal(1, Volatile.Read(ref prepareCount));
        await scope.RollbackAsync();
    }
}
