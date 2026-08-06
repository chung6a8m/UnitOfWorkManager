using UnitOfWork.Core;
using UnitOfWork.Tests.Fixtures;
using Xunit;

namespace UnitOfWork.Tests;

public class CommitRollbackIntegrationTests : Fixtures.UnitOfWorkTestBase
{
    private static UnitOfWorkManager CreateManager(SqliteTestDb db) =>
        new(db, (type, conn, tran) =>
        {
            if (type == typeof(ICounterRepository)) return new CounterRepository(conn);
            throw new NotSupportedException($"Repository chưa đăng ký: {type.Name}");
        });

    [Fact]
    public async Task Single_Level_Commit_Persists_Data()
    {
        using var db = new SqliteTestDb();
        var manager = CreateManager(db);

        var uow = await manager.BeginAsync();
        uow.GetRepository<ICounterRepository>().Insert(1);
        uow.GetRepository<ICounterRepository>().Insert(2);
        await uow.CommitAsync();
        uow.Dispose();
        manager.ClearCurrent();

        Assert.Equal(2, db.CountRows());
    }

    [Fact]
    public async Task Single_Level_Rollback_Discards_Data()
    {
        using var db = new SqliteTestDb();
        var manager = CreateManager(db);

        var uow = await manager.BeginAsync();
        uow.GetRepository<ICounterRepository>().Insert(1);
        await uow.RollbackAsync();
        uow.Dispose();
        manager.ClearCurrent();

        Assert.Equal(0, db.CountRows());
    }

    [Fact]
    public async Task Simulated_Nested_Service_Calls_Share_One_Real_Transaction()
    {
        using var db = new SqliteTestDb();
        var manager = CreateManager(db);

        await OuterServiceAsync(manager, db);
        manager.ClearCurrent();

        Assert.Equal(2, db.CountRows()); // outer(1) + inner(1) cùng commit chung 1 transaction
    }

    private static async Task OuterServiceAsync(IUnitOfWorkManager manager, SqliteTestDb db)
    {
        var uow = await manager.BeginAsync();
        uow.GetRepository<ICounterRepository>().Insert(1);

        await InnerServiceAsync(manager); // service khác tự BeginAsync() -> tái sử dụng cùng UoW

        await uow.CommitAsync();
        uow.Dispose();
    }

    private static async Task InnerServiceAsync(IUnitOfWorkManager manager)
    {
        var uow = await manager.BeginAsync();
        uow.GetRepository<ICounterRepository>().Insert(2);
        await uow.CommitAsync(); // chỉ giảm refcount, KHÔNG commit thật vì chưa phải outermost
    }

    [Fact]
    public async Task Simulated_Nested_Service_Failure_Rolls_Back_Everything()
    {
        using var db = new SqliteTestDb();
        var manager = CreateManager(db);

        var uow = await manager.BeginAsync();
        uow.GetRepository<ICounterRepository>().Insert(1); // "thành công" ở outer

        try
        {
            await FailingInnerServiceAsync(manager);
        }
        catch (InvalidOperationException)
        {
            // outer vẫn tưởng nó ổn và cố Commit — nhưng cờ rollback đã được set bởi inner
        }

        await uow.CommitAsync();
        uow.Dispose();
        manager.ClearCurrent();

        Assert.Equal(0, db.CountRows());
    }

    private static async Task FailingInnerServiceAsync(IUnitOfWorkManager manager)
    {
        var uow = await manager.BeginAsync();
        uow.GetRepository<ICounterRepository>().Insert(2);
        try
        {
            throw new InvalidOperationException("Lỗi nghiệp vụ giả lập ở tầng trong");
        }
        catch
        {
            await uow.RollbackAsync();
            throw;
        }
    }
}
