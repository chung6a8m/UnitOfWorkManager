using Dapper;
using UnitOfWork.Core;
using UnitOfWork.Core.Exceptions;
using UnitOfWork.Tests.Fixtures;
using Xunit;

namespace UnitOfWork.Tests;

public sealed class DapperQueryMultipleTests
{
    private static UnitOfWorkManager CreateManager(IDbConnectionFactory database) =>
        new(database, static (_, _) => new object());

    [Fact]
    public async Task Dqm01_QueryMultiple_Reads_Three_Grids_Under_One_Lease()
    {
        using var database = new SqliteTestDb();
        var manager = CreateManager(database);
        await using var scope = await manager.BeginAsync();

        using (var grid = scope.Connection.QueryMultiple(
            "SELECT 11; SELECT 22; SELECT 33;"))
        {
            Assert.Equal(11, grid.ReadSingle<int>());
            Assert.Equal(22, grid.ReadSingle<int>());
            Assert.Equal(33, grid.ReadSingle<int>());
        }

        Assert.Equal(1, await scope.Connection.ExecuteScalarAsync<int>("SELECT 1;"));
        await scope.RollbackAsync();
    }

    [Fact]
    public async Task Dqm02_QueryMultipleAsync_Reads_Three_Grids_And_Releases_Lease()
    {
        using var database = new SqliteTestDb();
        var manager = CreateManager(database);
        await using var scope = await manager.BeginAsync();

        await using (var grid = await scope.Connection.QueryMultipleAsync(
            new CommandDefinition("SELECT 11; SELECT 22; SELECT 33;")))
        {
            Assert.Equal(11, await grid.ReadSingleAsync<int>());
            Assert.Equal(22, await grid.ReadSingleAsync<int>());
            Assert.Equal(33, await grid.ReadSingleAsync<int>());
        }

        Assert.Equal(1, await scope.Connection.ExecuteScalarAsync<int>("SELECT 1;"));
        await scope.RollbackAsync();
    }

    [Fact]
    public async Task Dqm03_QueryMultiple_Rejects_Another_Command_While_GridReader_Is_Alive()
    {
        using var database = new SqliteTestDb();
        var manager = CreateManager(database);
        await using var scope = await manager.BeginAsync();

        using var grid = scope.Connection.QueryMultiple("SELECT 11; SELECT 22;");
        Assert.Equal(11, grid.ReadSingle<int>());

        await Assert.ThrowsAsync<UnitOfWorkConcurrencyException>(
            () => scope.Connection.ExecuteScalarAsync<int>("SELECT 1;"));

        Assert.Equal(22, grid.ReadSingle<int>());
        await scope.RollbackAsync();
    }

    [Fact]
    public async Task Dqm04_Disposed_GridReader_Allows_A_Subsequent_Command()
    {
        using var database = new SqliteTestDb();
        var manager = CreateManager(database);
        await using var scope = await manager.BeginAsync();

        using (var grid = scope.Connection.QueryMultiple("SELECT 11; SELECT 22;"))
            Assert.Equal(11, grid.ReadSingle<int>());

        Assert.Equal(1, await scope.Connection.ExecuteScalarAsync<int>("SELECT 1;"));
        await scope.RollbackAsync();
    }

    [Fact]
    public async Task Dqm05_Unbuffered_Read_Can_Break_Early_And_Release_Lease_On_Dispose()
    {
        using var database = new SqliteTestDb();
        var manager = CreateManager(database);
        await using var scope = await manager.BeginAsync();

        using (var grid = scope.Connection.QueryMultiple(
            "SELECT 11 UNION ALL SELECT 22 UNION ALL SELECT 33; SELECT 44;"))
        {
            foreach (var value in grid.Read<int>(buffered: false))
            {
                Assert.Equal(11, value);
                break;
            }
        }

        Assert.Equal(1, await scope.Connection.ExecuteScalarAsync<int>("SELECT 1;"));
        await scope.RollbackAsync();
    }

    [Fact]
    public async Task Dqm06_Disposing_Without_Reading_Remaining_Grids_Releases_Lease()
    {
        using var database = new SqliteTestDb();
        var manager = CreateManager(database);
        await using var scope = await manager.BeginAsync();

        using (var grid = scope.Connection.QueryMultiple(
            "SELECT 11; SELECT 22; SELECT 33;"))
        {
            Assert.Equal(11, grid.ReadSingle<int>());
        }

        Assert.Equal(1, await scope.Connection.ExecuteScalarAsync<int>("SELECT 1;"));
        await scope.RollbackAsync();
    }

    [Fact]
    public async Task Dqm07_ReadUnbufferedAsync_Cancellation_Does_Not_Leak_Lease()
    {
        var database = ScriptedMultiResultDbConnection.CreateBlockingReaderFixture();
        var manager = CreateManager(database);
        await using var scope = await manager.BeginAsync();

        using var cancellation = new CancellationTokenSource();
        await using (var grid = await scope.Connection.QueryMultipleAsync(
            new CommandDefinition("script", cancellationToken: cancellation.Token)))
        {
            var consumeTask = ConsumeAsync(grid.ReadUnbufferedAsync<int>());
            await database.ReadStarted;
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumeTask);
        }

        Assert.Equal(1, await scope.Connection.ExecuteScalarAsync<int>("scalar-one"));
        await scope.RollbackAsync();
    }

    [Fact]
    public async Task Dqm08_MultiMapping_Callback_Rejects_A_Nested_Command_And_Cleans_Up()
    {
        using var database = new SqliteTestDb();
        var manager = CreateManager(database);
        await using var scope = await manager.BeginAsync();

        using (var grid = scope.Connection.QueryMultiple(
            "SELECT 11 AS LeftValue, 22 AS RightValue;"))
        {
            var values = grid.Read<LeftRow, RightRow, int>(
                (left, right) =>
                {
                    Assert.Throws<UnitOfWorkConcurrencyException>(
                        () => scope.Connection.ExecuteScalar<int>("SELECT 1;"));
                    return left.LeftValue + right.RightValue;
                },
                splitOn: nameof(RightRow.RightValue)).AsList();

            Assert.Equal(new[] { 33 }, values);
        }

        Assert.Equal(1, await scope.Connection.ExecuteScalarAsync<int>("SELECT 1;"));
        await scope.RollbackAsync();
    }

    private static async Task ConsumeAsync(IAsyncEnumerable<int> values)
    {
        await foreach (var _ in values)
        {
        }
    }

    private sealed class LeftRow
    {
        public int LeftValue { get; init; }
    }

    private sealed class RightRow
    {
        public int RightValue { get; init; }
    }
}
