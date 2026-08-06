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
            t == typeof(ICounterRepository) ? new CounterRepository(c, tr) : throw new NotSupportedException());
        await uow.BeginTransactionAsync();

        Assert.Equal(uow.OwnerFlowId, CoreUoW.AmbientFlowId.Value);

        uow.Dispose();

        Assert.Null(CoreUoW.AmbientFlowId.Value);
    }

    [Fact]
    public async Task Dispose_Is_Idempotent()
    {
        using var db = new SqliteTestDb();
        var uow = new CoreUoW(db.CreateConnection(), (t, c, tr) =>
            t == typeof(ICounterRepository) ? new CounterRepository(c, tr) : throw new NotSupportedException());
        await uow.BeginTransactionAsync();

        uow.Dispose();
        var exception = Record.Exception(() => uow.Dispose());

        Assert.Null(exception);
        Assert.True(uow.IsDisposed);
    }

    [Fact]
    public async Task Manager_ClearCurrent_Allows_Fresh_BeginAsync_Afterwards()
    {
        using var db = new SqliteTestDb();
        var manager = new UnitOfWorkManager(db, (t, c, tr) =>
            t == typeof(ICounterRepository) ? new CounterRepository(c, tr) : throw new NotSupportedException());

        var first = await manager.BeginAsync();
        await first.CommitAsync();
        first.Dispose();
        manager.ClearCurrent();

        Assert.False(manager.HasCurrent);

        var second = await manager.BeginAsync();
        Assert.NotSame(first, second);

        await second.RollbackAsync();
        second.Dispose();
        manager.ClearCurrent();
    }
}
