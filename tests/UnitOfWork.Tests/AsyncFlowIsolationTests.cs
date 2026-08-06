using UnitOfWork.Core;
using CoreUoW = UnitOfWork.Core.UnitOfWork;
using UnitOfWork.Core.Exceptions;
using UnitOfWork.Tests.Fixtures;
using Xunit;

namespace UnitOfWork.Tests;

public class AsyncFlowIsolationTests : Fixtures.UnitOfWorkTestBase
{
    [Fact]
    public async Task Task_Run_Without_SuppressFlow_Can_Still_See_Same_Flow_And_Succeeds()
    {
        // AsyncLocal MẶC ĐỊNH flow xuyên qua Task.Run trong cùng execution context
        // -> đây là hành vi "vô tình chia sẻ" mà bài toán gốc cảnh báo. Test này xác nhận
        // guard KHÔNG chặn trường hợp hợp lệ (task con thật sự thuộc cùng flow logic).
        using var db = new SqliteTestDb();
        var uow = new CoreUoW(db.CreateConnection(), (t, c, tr) =>
            t == typeof(ICounterRepository) ? new CounterRepository(c, tr) : throw new NotSupportedException());
        await uow.BeginTransactionAsync();

        await Task.Run(() =>
        {
            // Cùng flow (AsyncLocal chảy vào Task.Run) -> không bị chặn
            uow.GetRepository<ICounterRepository>().Insert(1);
        });

        await uow.RollbackAsync();
        uow.Dispose();
    }

    [Fact]
    public async Task SuppressFlow_Isolated_Task_Cannot_See_Parent_UnitOfWork()
    {
        using var db = new SqliteTestDb();
        var uow = new CoreUoW(db.CreateConnection(), (t, c, tr) =>
            t == typeof(ICounterRepository) ? new CounterRepository(c, tr) : throw new NotSupportedException());
        await uow.BeginTransactionAsync();

        UnitOfWorkConcurrencyException? caught = null;

        await RunIsolatedAsync(() =>
        {
            try
            {
                // Task này không thấy AmbientFlowId của flow cha (đã bị SuppressFlow chặn)
                uow.GetRepository<ICounterRepository>();
            }
            catch (UnitOfWorkConcurrencyException ex)
            {
                caught = ex;
            }
        });

        Assert.NotNull(caught);
        Assert.Contains("AsyncLocal rỗng", caught!.Message);

        await uow.RollbackAsync();
        uow.Dispose();
    }

    [Fact]
    public async Task Different_Flow_With_Own_UnitOfWork_Cannot_Reuse_Foreign_Instance()
    {
        using var db = new SqliteTestDb();

        var uowA = new CoreUoW(db.CreateConnection(), (t, c, tr) =>
            t == typeof(ICounterRepository) ? new CounterRepository(c, tr) : throw new NotSupportedException());
        await uowA.BeginTransactionAsync();

        UnitOfWorkConcurrencyException? caught = null;

        // Chạy "flow B" độc lập: SuppressFlow rồi tự tạo UnitOfWork riêng (đúng cách),
        // nhưng bên trong cố tình (mô phỏng bug) gọi vào uowA của flow A.
        await RunIsolatedTaskAsync(async () =>
        {
            var uowB = new CoreUoW(db.CreateConnection(), (t, c, tr) =>
                t == typeof(ICounterRepository) ? new CounterRepository(c, tr) : throw new NotSupportedException());
            await uowB.BeginTransactionAsync(); // giờ AmbientFlowId trong flow B trỏ tới uowB

            try
            {
                uowA.GetRepository<ICounterRepository>(); // dùng nhầm UoW của flow A
            }
            catch (UnitOfWorkConcurrencyException ex)
            {
                caught = ex;
            }

            await uowB.RollbackAsync();
            uowB.Dispose();
        });

        Assert.NotNull(caught);
        Assert.Contains("flow khác", caught!.Message);

        await uowA.RollbackAsync();
        uowA.Dispose();
    }

    private static Task RunIsolatedAsync(Action action)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(action);
        }
    }

    private static Task RunIsolatedTaskAsync(Func<Task> action)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(action);
        }
    }
}
