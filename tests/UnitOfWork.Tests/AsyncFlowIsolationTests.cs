using UnitOfWork.Core;
using UnitOfWork.Core.Exceptions;
using UnitOfWork.Tests.Fixtures;
using Xunit;

namespace UnitOfWork.Tests;

public class AsyncFlowIsolationTests
{
    private static UnitOfWorkManager CreateManager(IDbConnectionFactory factory) =>
        new(factory, (type, connection) =>
            type == typeof(ICounterRepository)
                ? new CounterRepository(connection)
                : throw new NotSupportedException());

    [Fact]
    public async Task Inherited_Execution_Context_Can_Use_Retained_Scope_Sequentially()
    {
        using var db = new SqliteTestDb();
        var manager = CreateManager(db);
        using var scope = await manager.BeginAsync();

        await Task.Run(() => scope.GetRepository<ICounterRepository>().Insert(1));

        await scope.CompleteAsync();
        Assert.Equal(1, db.CountRows());
    }

    [Fact]
    public async Task Suppressed_Execution_Context_Cannot_Use_Retained_Scope()
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
