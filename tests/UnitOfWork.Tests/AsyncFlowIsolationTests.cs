using UnitOfWork.Core;
using UnitOfWork.Core.Exceptions;
using UnitOfWork.Tests.Fixtures;
using Xunit;

namespace UnitOfWork.Tests;

public class AsyncFlowIsolationTests
{
    public enum RetainedScopeSettlement
    {
        Complete,
        Rollback,
        Dispose
    }

    private static UnitOfWorkManager CreateManager(IDbConnectionFactory factory) =>
        new(factory, (type, connection) =>
            type == typeof(ICounterRepository)
                ? new CounterRepository(connection)
                : throw new NotSupportedException());

    [Fact]
    public async Task Inherited_Task_Can_Use_Current_Root_Sequentially()
    {
        using var db = new SqliteTestDb();
        var manager = CreateManager(db);
        using var scope = await manager.BeginAsync();

        await Task.Run(() => manager.Current.GetRepository<ICounterRepository>().Insert(1));
        await Task.Run(() => manager.Current.GetRepository<ICounterRepository>().Insert(2));

        await scope.CompleteAsync();
        Assert.Equal(2, db.CountRows());
    }

    [Fact]
    public async Task Retained_Scope_After_Root_Finalization_Is_Rejected()
    {
        var connection = new ControlledDbConnection(initiallyOpen: true);
        var manager = CreateManager(new ControlledConnectionFactory(connection));
        var scope = await manager.BeginAsync();

        await scope.CompleteAsync();

        Assert.Throws<UnitOfWorkStateException>(() => scope.GetRepository<ICounterRepository>());
    }

    [Fact]
    public async Task Suppressed_Flow_Cannot_Use_Retained_Scope()
    {
        var connection = new ControlledDbConnection(initiallyOpen: true);
        var manager = CreateManager(new ControlledConnectionFactory(connection));
        using var scope = await manager.BeginAsync();

        var exception = await RunIsolatedAsync(
            () => Record.Exception(() => scope.GetRepository<ICounterRepository>()));

        var concurrencyException = Assert.IsType<UnitOfWorkConcurrencyException>(exception);
        Assert.Contains(
            "current root for this manager is missing or foreign",
            concurrencyException.Message,
            StringComparison.OrdinalIgnoreCase);

        await scope.RollbackAsync();
    }

    [Fact]
    public async Task Foreign_Current_Root_Cannot_Use_Retained_Parent_Scope()
    {
        var parentConnection = new ControlledDbConnection(initiallyOpen: true);
        var childConnection = new ControlledDbConnection(initiallyOpen: true);
        var manager = CreateManager(new ControlledConnectionFactory(parentConnection, childConnection));
        using var parentScope = await manager.BeginAsync();

        var exception = await RunIsolatedAsync(async () =>
        {
            using var childScope = await manager.BeginAsync();
            var actual = Record.Exception(() => parentScope.GetRepository<ICounterRepository>());
            await childScope.RollbackAsync();
            return actual;
        });

        var concurrencyException = Assert.IsType<UnitOfWorkConcurrencyException>(exception);
        Assert.Contains(
            "current root for this manager is missing or foreign",
            concurrencyException.Message,
            StringComparison.OrdinalIgnoreCase);

        Assert.True(manager.HasCurrent);
        await parentScope.RollbackAsync();
    }

    [Theory]
    [InlineData(RetainedScopeSettlement.Complete)]
    [InlineData(RetainedScopeSettlement.Rollback)]
    [InlineData(RetainedScopeSettlement.Dispose)]
    public async Task Suppressed_Execution_Context_Cannot_Settle_Retained_Scope_Without_Mutation(
        RetainedScopeSettlement settlement)
    {
        var connection = new ControlledDbConnection(initiallyOpen: true);
        var manager = CreateManager(new ControlledConnectionFactory(connection));
        var scope = await manager.BeginAsync();
        var ownerContext = manager.Current;
        var root = Assert.IsType<RootUnitOfWork>(ownerContext);

        var exception = await RunIsolatedAsync(
            () => Record.ExceptionAsync(() => SettleAsync(scope, settlement)));

        AssertRejectedSettlementDidNotMutateOwner(
            exception,
            manager,
            ownerContext,
            root,
            connection);

        await SettleAsync(scope, settlement);

        AssertOwnerSettlementFinalized(settlement, manager, root, connection);
        scope.Dispose();
    }

    [Theory]
    [InlineData(RetainedScopeSettlement.Complete)]
    [InlineData(RetainedScopeSettlement.Rollback)]
    [InlineData(RetainedScopeSettlement.Dispose)]
    public async Task Foreign_Current_Root_Cannot_Settle_Retained_Parent_Scope_Without_Mutation(
        RetainedScopeSettlement settlement)
    {
        var parentConnection = new ControlledDbConnection(initiallyOpen: true);
        var childConnection = new ControlledDbConnection(initiallyOpen: true);
        var manager = CreateManager(
            new ControlledConnectionFactory(parentConnection, childConnection));
        var parentScope = await manager.BeginAsync();
        var ownerContext = manager.Current;
        var parentRoot = Assert.IsType<RootUnitOfWork>(ownerContext);

        var exception = await RunIsolatedAsync(async () =>
        {
            using var childScope = await manager.BeginAsync();
            var actual = await Record.ExceptionAsync(
                () => SettleAsync(parentScope, settlement));
            await childScope.RollbackAsync();
            return actual;
        });

        AssertRejectedSettlementDidNotMutateOwner(
            exception,
            manager,
            ownerContext,
            parentRoot,
            parentConnection);
        Assert.True(childConnection.IsDisposed);
        Assert.Equal(1, childConnection.LastTransaction!.RollbackCount);

        await SettleAsync(parentScope, settlement);

        AssertOwnerSettlementFinalized(
            settlement,
            manager,
            parentRoot,
            parentConnection);
        parentScope.Dispose();
    }

    [Theory]
    [InlineData(RetainedScopeSettlement.Complete, RetainedScopeSettlement.Complete)]
    [InlineData(RetainedScopeSettlement.Complete, RetainedScopeSettlement.Rollback)]
    [InlineData(RetainedScopeSettlement.Rollback, RetainedScopeSettlement.Complete)]
    [InlineData(RetainedScopeSettlement.Rollback, RetainedScopeSettlement.Rollback)]
    public async Task Already_Settled_Scope_Rejects_Repeated_Async_Settlement_After_Manager_Finalization(
        RetainedScopeSettlement initialSettlement,
        RetainedScopeSettlement repeatedSettlement)
    {
        var connection = new ControlledDbConnection(initiallyOpen: true);
        var manager = CreateManager(new ControlledConnectionFactory(connection));
        var scope = await manager.BeginAsync();
        var root = Assert.IsType<RootUnitOfWork>(manager.Current);

        await SettleAsync(scope, initialSettlement);

        await Assert.ThrowsAsync<UnitOfWorkStateException>(
            () => SettleAsync(scope, repeatedSettlement));
        AssertOwnerSettlementFinalized(initialSettlement, manager, root, connection);

        scope.Dispose();

        Assert.Equal(1, connection.LastTransaction!.DisposeCount);
        Assert.Equal(
            initialSettlement == RetainedScopeSettlement.Complete ? 1 : 0,
            connection.LastTransaction.CommitCount);
        Assert.Equal(
            initialSettlement == RetainedScopeSettlement.Rollback ? 1 : 0,
            connection.LastTransaction.RollbackCount);
    }

    private static Task SettleAsync(
        IUnitOfWorkScope scope,
        RetainedScopeSettlement settlement) => settlement switch
    {
        RetainedScopeSettlement.Complete => scope.CompleteAsync(),
        RetainedScopeSettlement.Rollback => scope.RollbackAsync(),
        RetainedScopeSettlement.Dispose => DisposeAsync(scope),
        _ => throw new ArgumentOutOfRangeException(nameof(settlement))
    };

    private static Task DisposeAsync(IUnitOfWorkScope scope)
    {
        scope.Dispose();
        return Task.CompletedTask;
    }

    private static void AssertRejectedSettlementDidNotMutateOwner(
        Exception? exception,
        UnitOfWorkManager manager,
        IUnitOfWorkContext ownerContext,
        RootUnitOfWork root,
        ControlledDbConnection connection)
    {
        var concurrencyException = Assert.IsType<UnitOfWorkConcurrencyException>(exception);
        Assert.Contains(
            "current root for this manager is missing or foreign",
            concurrencyException.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(manager.HasCurrent);
        Assert.Same(ownerContext, manager.Current);
        Assert.Equal(1, root.ActiveScopeCount);
        Assert.False(root.RollbackRequested);
        Assert.Equal(UnitOfWorkLifecycleState.Active, root.LifecycleState);
        Assert.Equal(UnitOfWorkCompletionOutcome.None, root.CompletionOutcome);
        Assert.False(connection.IsDisposed);
        Assert.Equal(0, connection.LastTransaction!.CommitCount);
        Assert.Equal(0, connection.LastTransaction.RollbackCount);
        Assert.Equal(0, connection.LastTransaction.DisposeCount);
    }

    private static void AssertOwnerSettlementFinalized(
        RetainedScopeSettlement settlement,
        UnitOfWorkManager manager,
        RootUnitOfWork root,
        ControlledDbConnection connection)
    {
        Assert.False(manager.HasCurrent);
        Assert.Equal(0, root.ActiveScopeCount);
        Assert.Equal(UnitOfWorkLifecycleState.Disposed, root.LifecycleState);
        Assert.True(connection.IsDisposed);
        Assert.Equal(1, connection.LastTransaction!.DisposeCount);

        if (settlement == RetainedScopeSettlement.Complete)
        {
            Assert.Equal(UnitOfWorkCompletionOutcome.Committed, root.CompletionOutcome);
            Assert.Equal(1, connection.LastTransaction.CommitCount);
            Assert.Equal(0, connection.LastTransaction.RollbackCount);
        }
        else
        {
            Assert.Equal(UnitOfWorkCompletionOutcome.RolledBack, root.CompletionOutcome);
            Assert.Equal(0, connection.LastTransaction.CommitCount);
            Assert.Equal(1, connection.LastTransaction.RollbackCount);
        }
    }

    private static Task<T> RunIsolatedAsync<T>(Func<T> action)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(action);
        }
    }

    private static Task<T> RunIsolatedAsync<T>(Func<Task<T>> action)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(action);
        }
    }
}
