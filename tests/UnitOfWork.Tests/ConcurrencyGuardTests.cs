using UnitOfWork.Core;
using CoreUoW = UnitOfWork.Core.UnitOfWork;
using UnitOfWork.Core.Exceptions;
using UnitOfWork.Tests.Fixtures;
using Xunit;

namespace UnitOfWork.Tests;

public class ConcurrencyGuardTests : Fixtures.UnitOfWorkTestBase
{
    private static CoreUoW NewOwnedUnitOfWork(SqliteTestDb db)
    {
        // Tạo trực tiếp (không qua Manager) để nắm chắc AmbientFlowId đang trỏ đúng instance
        // này trong luồng gọi hiện tại.
        var uow = new CoreUoW(db.CreateConnection(), (t, c, tr) =>
            t == typeof(ICounterRepository)
                ? new CounterRepository(c, tr)
                : throw new NotSupportedException());
        return uow;
    }

    [Fact]
    public async Task Two_Concurrent_Guarded_Operations_On_Same_UoW_Throw()
    {
        using var db = new SqliteTestDb();
        var uow = NewOwnedUnitOfWork(db);
        await uow.BeginTransactionAsync();

        // Giả lập 1 thao tác "chậm" đang chiếm giữ guard...
        var slowOp = uow.RunGuardedAsync(async () =>
        {
            await Task.Delay(300);
            return true;
        });

        await Task.Delay(50); // đảm bảo slowOp đã vào bên trong guard trước

        // ...trong khi thao tác thứ 2 cố chạy đồng thời trên CÙNG uow -> phải bị chặn ngay lập tức
        var ex = await Assert.ThrowsAsync<UnitOfWorkConcurrencyException>(
            () => uow.RunGuardedAsync(() => Task.FromResult(true)));

        Assert.Contains("đồng thời", ex.Message);

        await slowOp; // dọn dẹp thao tác đầu tiên
        await uow.RollbackAsync();
        uow.Dispose();
    }

    [Fact]
    public async Task Guard_Is_Released_After_Operation_Completes_So_Sequential_Calls_Succeed()
    {
        using var db = new SqliteTestDb();
        var uow = NewOwnedUnitOfWork(db);
        await uow.BeginTransactionAsync();

        await uow.RunGuardedAsync(() => Task.FromResult(true));
        // Gọi tuần tự (không chồng nhau) -> không được ném exception
        var result = await uow.RunGuardedAsync(() => Task.FromResult(42));

        Assert.Equal(42, result);
        await uow.RollbackAsync();
        uow.Dispose();
    }

    [Fact]
    public async Task Guard_Also_Protects_Repository_Calls_Through_GuardedDbConnection()
    {
        using var db = new SqliteTestDb();
        var uow = NewOwnedUnitOfWork(db);
        await uow.BeginTransactionAsync();

        var repo = uow.GetRepository<ICounterRepository>();

        // Chiếm guard bằng 1 thao tác "chậm" giả lập
        var slowOp = uow.RunGuardedAsync(async () =>
        {
            await Task.Delay(300);
            return true;
        });
        await Task.Delay(50);

        // Insert() thật sự chạy qua GuardedDbCommand.ExecuteNonQuery -> cũng phải bị chặn
        var ex = Assert.Throws<UnitOfWorkConcurrencyException>(() => repo.Insert(1));
        Assert.Contains("đồng thời", ex.Message);

        await slowOp;
        await uow.RollbackAsync();
        uow.Dispose();
    }

    [Fact]
    public async Task Operation_After_Dispose_Throws_ObjectDisposedException()
    {
        using var db = new SqliteTestDb();
        var uow = NewOwnedUnitOfWork(db);
        await uow.BeginTransactionAsync();
        uow.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => uow.CommitAsync());
        Assert.Throws<ObjectDisposedException>(() => uow.GetRepository<ICounterRepository>());
    }
}
