using UnitOfWork.Core;
using UnitOfWork.Tests.Fixtures;
using Xunit;

namespace UnitOfWork.Tests;

public class RefCountingTests : Fixtures.UnitOfWorkTestBase
{
    private static UnitOfWorkManager CreateManager(SqliteTestDb db) =>
        new(db, (type, connection) =>
        {
            if (type == typeof(ICounterRepository)) return new CounterRepository(connection);
            throw new NotSupportedException($"Repository chưa đăng ký: {type.Name}");
        });

    [Fact]
    public async Task Nested_BeginAsync_Returns_Distinct_Scopes()
    {
        using var db = new SqliteTestDb();
        var manager = CreateManager(db);

        using var outer = await manager.BeginAsync();
        using var inner = await manager.BeginAsync();

        Assert.NotSame(outer, inner);
        Assert.Same(outer.Connection, inner.Connection);

        await inner.CompleteAsync();
        await outer.CompleteAsync();
    }

    [Fact]
    public async Task Complete_Only_Persists_When_Outermost_Scope_Completes()
    {
        using var db = new SqliteTestDb();
        var manager = CreateManager(db);

        using var outer = await manager.BeginAsync();
        outer.GetRepository<ICounterRepository>().Insert(1);

        using var inner = await manager.BeginAsync();
        await inner.CompleteAsync();

        Assert.Equal(0, db.CountRows());

        await outer.CompleteAsync();

        Assert.Equal(1, db.CountRows());
    }

    [Fact]
    public async Task Inner_Rollback_Forces_Outer_To_Rollback_Everything()
    {
        using var db = new SqliteTestDb();
        var manager = CreateManager(db);

        using var outer = await manager.BeginAsync();
        outer.GetRepository<ICounterRepository>().Insert(1);

        using var inner = await manager.BeginAsync();
        inner.GetRepository<ICounterRepository>().Insert(2);
        await inner.RollbackAsync();

        await outer.CompleteAsync();

        Assert.Equal(0, db.CountRows());
    }

    [Fact]
    public async Task Rollback_Request_Remains_Effective_Until_Outermost_Exit()
    {
        using var db = new SqliteTestDb();
        var manager = CreateManager(db);

        using var outer = await manager.BeginAsync();
        outer.GetRepository<ICounterRepository>().Insert(1);
        using var inner = await manager.BeginAsync();

        await inner.RollbackAsync();

        Assert.True(manager.HasCurrent);

        await outer.CompleteAsync();

        Assert.False(manager.HasCurrent);
        Assert.Equal(0, db.CountRows());
    }
}
