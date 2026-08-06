using UnitOfWork.Core;
using CoreUoW = UnitOfWork.Core.UnitOfWork;
using UnitOfWork.Tests.Fixtures;
using Xunit;

namespace UnitOfWork.Tests;

public class DisposalTests : Fixtures.UnitOfWorkTestBase
{
    [Fact]
    public async Task Dispose_Clears_AmbientFlowId_Owned_By_This_Instance()
    {
        using var db = new SqliteTestDb();
        var uow = new CoreUoW(db.CreateConnection(), (t, c, tr) =>
            t == typeof(ICounterRepository) ? new CounterRepository(c) : throw new NotSupportedException());
        await uow.BeginTransactionAsync();

        Assert.Equal(uow.OwnerFlowId, CoreUoW.AmbientFlowId);

        uow.Dispose();

        Assert.Null(CoreUoW.AmbientFlowId);
    }

    [Fact]
    public async Task Dispose_Is_Idempotent()
    {
        using var db = new SqliteTestDb();
        var uow = new CoreUoW(db.CreateConnection(), (t, c, tr) =>
            t == typeof(ICounterRepository) ? new CounterRepository(c) : throw new NotSupportedException());
        await uow.BeginTransactionAsync();

        uow.Dispose();
        var exception = Record.Exception(() => uow.Dispose());

        Assert.Null(exception);
        Assert.True(uow.IsDisposed);
    }

    [Fact]
    public async Task Manager_Finalization_Allows_Fresh_BeginAsync_Afterwards()
    {
        using var db = new SqliteTestDb();
        var manager = new UnitOfWorkManager(db, (t, c) =>
            t == typeof(ICounterRepository) ? new CounterRepository(c) : throw new NotSupportedException());

        using var first = await manager.BeginAsync();
        await first.CompleteAsync();

        Assert.False(manager.HasCurrent);

        using var second = await manager.BeginAsync();
        Assert.NotSame(first, second);

        await second.RollbackAsync();
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
        var manager = new UnitOfWorkManager(
            factory,
            (_, _) => throw new NotSupportedException());
        var first = await manager.BeginAsync();

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => first.CompleteAsync());

        Assert.Same(commitFailure, actual);
        Assert.False(manager.HasCurrent);
        Assert.Throws<InvalidOperationException>(() => manager.Current);

        using var second = await manager.BeginAsync();

        Assert.Equal(2, factory.CreateCount);
        Assert.Same(freshConnection, ((RootUnitOfWork)manager.Current).Transaction!.Connection);
        await second.RollbackAsync();
    }
}
