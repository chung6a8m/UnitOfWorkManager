using UnitOfWork.Core;
using CoreUoW = UnitOfWork.Core.UnitOfWork;
using UnitOfWork.Tests.Fixtures;
using Xunit;

namespace UnitOfWork.Tests;

public class RefCountingTests : Fixtures.UnitOfWorkTestBase
{
    private static UnitOfWorkManager CreateManager(SqliteTestDb db) =>
        new(db, (type, conn, tran) =>
        {
            if (type == typeof(ICounterRepository)) return new CounterRepository(conn, tran);
            throw new NotSupportedException($"Repository chưa đăng ký: {type.Name}");
        });

    [Fact]
    public async Task Nested_BeginAsync_Reuses_Same_Instance()
    {
        using var db = new SqliteTestDb();
        var manager = CreateManager(db);

        var outer = await manager.BeginAsync();
        var inner = await manager.BeginAsync(); // lồng trong cùng flow -> phải là CÙNG instance

        Assert.Same(outer, inner);

        await inner.CommitAsync();
        await outer.CommitAsync();
        outer.Dispose(); // outer == inner (cùng instance)
        manager.ClearCurrent();
    }

    [Fact]
    public async Task Commit_Only_Persists_When_Outermost_Call_Completes()
    {
        using var db = new SqliteTestDb();
        var manager = CreateManager(db);

        var outer = await manager.BeginAsync();
        outer.GetRepository<ICounterRepository>().Insert(1);

        var inner = await manager.BeginAsync(); // refcount = 2
        await inner.CommitAsync();              // refcount -> 1, CHƯA commit thật

        Assert.Equal(0, db.CountRows()); // đọc bằng connection khác -> chưa thấy gì vì transaction chưa commit

        await outer.CommitAsync();               // refcount -> 0, commit thật
        outer.Dispose();
        manager.ClearCurrent();

        Assert.Equal(1, db.CountRows());
    }

    [Fact]
    public async Task Inner_Rollback_Forces_Outer_To_Rollback_Everything()
    {
        using var db = new SqliteTestDb();
        var manager = CreateManager(db);

        var outer = await manager.BeginAsync();
        outer.GetRepository<ICounterRepository>().Insert(1); // "thành công" ở tầng ngoài

        var inner = await manager.BeginAsync();
        inner.GetRepository<ICounterRepository>().Insert(2);
        await inner.RollbackAsync(); // tầng trong lỗi -> đánh dấu rollback toàn bộ

        // Tầng ngoài vẫn "tưởng" mình thành công và gọi CommitAsync(),
        // nhưng vì đã có cờ rollback nên transaction thật vẫn bị hủy.
        await outer.CommitAsync();
        outer.Dispose();
        manager.ClearCurrent();

        Assert.Equal(0, db.CountRows());
    }

    [Fact]
    public async Task Rollback_Requested_Flag_Is_Visible_Before_Outermost_Exit()
    {
        using var db = new SqliteTestDb();
        var manager = CreateManager(db);

        var outer = (CoreUoW)await manager.BeginAsync();
        var inner = await manager.BeginAsync();
        await inner.RollbackAsync();

        Assert.True(outer.RollbackRequested);
        Assert.Equal(1, outer.RefCount);

        await outer.RollbackAsync();
        outer.Dispose();
        manager.ClearCurrent();
    }
}
