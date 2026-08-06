using UnitOfWork.Core;
using UnitOfWork.Tests.Fixtures;
using Xunit;

namespace UnitOfWork.Tests;

public class CommitRollbackIntegrationTests
{
    private static UnitOfWorkManager CreateManager(SqliteTestDb db) =>
        new(db, (type, conn) =>
        {
            if (type == typeof(ICounterRepository)) return new CounterRepository(conn);
            throw new NotSupportedException($"Repository chưa đăng ký: {type.Name}");
        });

    [Fact]
    public async Task Single_Level_Commit_Persists_Data()
    {
        using var db = new SqliteTestDb();
        var manager = CreateManager(db);

        using var scope = await manager.BeginAsync();
        scope.GetRepository<ICounterRepository>().Insert(1);
        scope.GetRepository<ICounterRepository>().Insert(2);
        await scope.CompleteAsync();

        Assert.Equal(2, db.CountRows());
    }

    [Fact]
    public async Task Single_Level_Rollback_Discards_Data()
    {
        using var db = new SqliteTestDb();
        var manager = CreateManager(db);

        using var scope = await manager.BeginAsync();
        scope.GetRepository<ICounterRepository>().Insert(1);
        await scope.RollbackAsync();

        Assert.Equal(0, db.CountRows());
    }

    [Fact]
    public async Task Simulated_Nested_Service_Calls_Share_One_Real_Transaction()
    {
        using var db = new SqliteTestDb();
        var manager = CreateManager(db);

        await OuterServiceAsync(manager);

        Assert.Equal(2, db.CountRows()); // outer(1) + inner(1) cùng commit chung 1 transaction
    }

    private static async Task OuterServiceAsync(IUnitOfWorkManager manager)
    {
        using var scope = await manager.BeginAsync();
        scope.GetRepository<ICounterRepository>().Insert(1);

        await InnerServiceAsync(manager); // service khác nhận lease riêng trên cùng root

        await scope.CompleteAsync();
    }

    private static async Task InnerServiceAsync(IUnitOfWorkManager manager)
    {
        using var scope = await manager.BeginAsync();
        scope.GetRepository<ICounterRepository>().Insert(2);
        await scope.CompleteAsync(); // chỉ settle lease, chưa commit thật vì outer còn active
    }

    [Fact]
    public async Task Simulated_Nested_Service_Failure_Rolls_Back_Everything()
    {
        using var db = new SqliteTestDb();
        var manager = CreateManager(db);

        using var scope = await manager.BeginAsync();
        scope.GetRepository<ICounterRepository>().Insert(1); // "thành công" ở outer

        try
        {
            await FailingInnerServiceAsync(manager);
        }
        catch (InvalidOperationException)
        {
            // outer vẫn tưởng nó ổn và cố Commit — nhưng cờ rollback đã được set bởi inner
        }

        await scope.CompleteAsync();

        Assert.Equal(0, db.CountRows());
    }

    private static async Task FailingInnerServiceAsync(IUnitOfWorkManager manager)
    {
        using var scope = await manager.BeginAsync();
        scope.GetRepository<ICounterRepository>().Insert(2);
        try
        {
            throw new InvalidOperationException("Lỗi nghiệp vụ giả lập ở tầng trong");
        }
        catch
        {
            await scope.RollbackAsync();
            throw;
        }
    }
}
