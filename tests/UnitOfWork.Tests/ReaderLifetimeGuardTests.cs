using System.Data;
using System.Data.Common;
using UnitOfWork.Core;
using UnitOfWork.Core.Exceptions;
using UnitOfWork.Tests.Fixtures;
using Xunit;

namespace UnitOfWork.Tests;

public class ReaderLifetimeGuardTests
{
    [Fact]
    public async Task Sqlite_Async_Reader_Blocks_Second_Command_Until_Disposed()
    {
        using var database = new SqliteTestDb();
        var manager = new UnitOfWorkManager(database, (_, _) => throw new NotSupportedException());
        await using var scope = await manager.BeginAsync();
        await using var readerCommand = scope.Connection.CreateCommand();
        await using var secondCommand = scope.Connection.CreateCommand();
        readerCommand.CommandText = "SELECT Value FROM Counter;";
        secondCommand.CommandText = "SELECT COUNT(*) FROM Counter;";

        var reader = await readerCommand.ExecuteReaderAsync();

        await Assert.ThrowsAsync<UnitOfWorkConcurrencyException>(
            () => secondCommand.ExecuteScalarAsync());

        await reader.DisposeAsync();
        Assert.Equal(0L, await secondCommand.ExecuteScalarAsync());
        await scope.RollbackAsync();
    }

    [Fact]
    public async Task Reader_Holds_Operation_Lease_Until_Dispose()
    {
        var reader = new ControlledDbDataReader();
        var manager = CreateManager(reader);
        await using var scope = await manager.BeginAsync();
        await using var readerCommand = scope.Connection.CreateCommand();
        await using var overlappingCommand = scope.Connection.CreateCommand();

        using (readerCommand.ExecuteReader())
        {
            Assert.Throws<UnitOfWorkConcurrencyException>(overlappingCommand.ExecuteScalar);
        }

        await overlappingCommand.ExecuteScalarAsync();
        await scope.RollbackAsync();
    }

    [Fact]
    public async Task Reader_Holds_Operation_Lease_Until_DisposeAsync()
    {
        var reader = new ControlledDbDataReader();
        var manager = CreateManager(reader);
        await using var scope = await manager.BeginAsync();
        await using var readerCommand = scope.Connection.CreateCommand();
        await using var overlappingCommand = scope.Connection.CreateCommand();

        var wrappedReader = await readerCommand.ExecuteReaderAsync();

        await Assert.ThrowsAsync<UnitOfWorkConcurrencyException>(
            () => overlappingCommand.ExecuteScalarAsync());
        await Assert.ThrowsAsync<UnitOfWorkConcurrencyException>(
            () => scope.CompleteAsync());

        await wrappedReader.DisposeAsync();
        await overlappingCommand.ExecuteScalarAsync();
        await scope.RollbackAsync();
    }

    [Fact]
    public async Task Reader_Close_Releases_Operation_Lease()
    {
        var reader = new ControlledDbDataReader();
        var manager = CreateManager(reader);
        await using var scope = await manager.BeginAsync();
        await using var command = scope.Connection.CreateCommand();
        await using var overlappingCommand = scope.Connection.CreateCommand();

        var wrappedReader = command.ExecuteReader();
        wrappedReader.Close();
        wrappedReader.Close();

        Assert.Equal(1, reader.CloseCount);
        await overlappingCommand.ExecuteScalarAsync();
        await scope.RollbackAsync();
    }

    [Fact]
    public async Task Reader_Dispose_Releases_Lease_Even_When_Inner_Dispose_Throws()
    {
        var reader = new ControlledDbDataReader(disposeException: new InvalidOperationException("dispose failed"));
        var manager = CreateManager(reader);
        await using var scope = await manager.BeginAsync();
        await using var command = scope.Connection.CreateCommand();
        await using var overlappingCommand = scope.Connection.CreateCommand();

        var wrappedReader = command.ExecuteReader();

        Assert.Throws<InvalidOperationException>(wrappedReader.Dispose);
        wrappedReader.Dispose();

        Assert.Equal(1, reader.DisposeCount);
        await overlappingCommand.ExecuteScalarAsync();
        await scope.RollbackAsync();
    }

    [Fact]
    public async Task Reader_DisposeAsync_Releases_Lease_Even_When_Inner_DisposeAsync_Throws()
    {
        var reader = new ControlledDbDataReader(disposeAsyncException: new InvalidOperationException("async dispose failed"));
        var manager = CreateManager(reader);
        await using var scope = await manager.BeginAsync();
        await using var command = scope.Connection.CreateCommand();
        await using var overlappingCommand = scope.Connection.CreateCommand();

        var wrappedReader = await command.ExecuteReaderAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await wrappedReader.DisposeAsync());
        await wrappedReader.DisposeAsync();

        Assert.Equal(1, reader.DisposeAsyncCount);
        await overlappingCommand.ExecuteScalarAsync();
        await scope.RollbackAsync();
    }

    [Fact]
    public async Task Reader_Creation_Failure_Releases_Operation_Lease()
    {
        var connection = new ControlledDbConnection(
            initiallyOpen: true,
            commandFactory: inner => new ControlledDbCommand(
                inner,
                () => 1L,
                executeReader: _ => throw new InvalidOperationException("creation failed")));
        var manager = CreateManager(connection);
        await using var scope = await manager.BeginAsync();
        await using var command = scope.Connection.CreateCommand();
        await using var overlappingCommand = scope.Connection.CreateCommand();

        Assert.Throws<InvalidOperationException>(command.ExecuteReader);

        await overlappingCommand.ExecuteScalarAsync();
        await scope.RollbackAsync();
    }

    [Fact]
    public async Task Reader_Creation_Cancellation_Releases_Operation_Lease()
    {
        using var cancellation = new CancellationTokenSource();
        var connection = new ControlledDbConnection(
            initiallyOpen: true,
            commandFactory: inner => new ControlledDbCommand(
                inner,
                () => 1L,
                executeReaderAsync: async (_, token) =>
                {
                    await Task.Yield();
                    token.ThrowIfCancellationRequested();
                    return new ControlledDbDataReader();
                }));
        var manager = CreateManager(connection);
        await using var scope = await manager.BeginAsync();
        await using var command = scope.Connection.CreateCommand();
        await using var overlappingCommand = scope.Connection.CreateCommand();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => command.ExecuteReaderAsync(cancellation.Token));

        await overlappingCommand.ExecuteScalarAsync();
        await scope.RollbackAsync();
    }

    [Fact]
    public async Task ReadAsync_Uses_Provider_Async_API_And_Forwards_Token()
    {
        using var cancellation = new CancellationTokenSource();
        var reader = new ControlledDbDataReader();
        var manager = CreateManager(reader);
        await using var scope = await manager.BeginAsync();
        await using var command = scope.Connection.CreateCommand();

        var wrappedReader = await command.ExecuteReaderAsync();
        await wrappedReader.ReadAsync(cancellation.Token);

        Assert.Equal(0, reader.ReadCount);
        Assert.Equal(1, reader.ReadAsyncCount);
        Assert.Equal(cancellation.Token, reader.LastReadCancellationToken);
        await wrappedReader.DisposeAsync();
        await scope.RollbackAsync();
    }

    [Fact]
    public async Task NextResultAsync_Uses_Provider_Async_API_And_Forwards_Token()
    {
        using var cancellation = new CancellationTokenSource();
        var reader = new ControlledDbDataReader();
        var manager = CreateManager(reader);
        await using var scope = await manager.BeginAsync();
        await using var command = scope.Connection.CreateCommand();

        var wrappedReader = await command.ExecuteReaderAsync();
        await wrappedReader.NextResultAsync(cancellation.Token);

        Assert.Equal(0, reader.NextResultCount);
        Assert.Equal(1, reader.NextResultAsyncCount);
        Assert.Equal(cancellation.Token, reader.LastNextResultCancellationToken);
        await wrappedReader.DisposeAsync();
        await scope.RollbackAsync();
    }

    [Fact]
    public async Task CloseAsync_Uses_Provider_Async_API_And_Releases_Lease_Once()
    {
        var reader = new ControlledDbDataReader { ThrowOnSynchronousApis = true };
        var manager = CreateManager(reader);
        await using var scope = await manager.BeginAsync();
        await using var command = scope.Connection.CreateCommand();
        await using var overlappingCommand = scope.Connection.CreateCommand();

        var wrappedReader = await command.ExecuteReaderAsync();
        await wrappedReader.CloseAsync();
        await wrappedReader.CloseAsync();

        Assert.Equal(0, reader.CloseCount);
        Assert.Equal(1, reader.CloseAsyncCount);
        await overlappingCommand.ExecuteScalarAsync();
        await scope.RollbackAsync();
    }

    [Fact]
    public async Task CloseAsync_Releases_Lease_Once_When_Provider_CloseAsync_Fails()
    {
        var reader = new ControlledDbDataReader(
            closeAsyncException: new InvalidOperationException("async close failed"))
        {
            ThrowOnSynchronousApis = true
        };
        var manager = CreateManager(reader);
        await using var scope = await manager.BeginAsync();
        await using var command = scope.Connection.CreateCommand();
        await using var overlappingCommand = scope.Connection.CreateCommand();

        var wrappedReader = await command.ExecuteReaderAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => wrappedReader.CloseAsync());
        await wrappedReader.CloseAsync();

        Assert.Equal(0, reader.CloseCount);
        Assert.Equal(1, reader.CloseAsyncCount);
        await overlappingCommand.ExecuteScalarAsync();
        await scope.RollbackAsync();
    }

    [Fact]
    public async Task GetFieldValueAsync_Uses_Provider_Async_API_And_Keeps_Lease()
    {
        using var cancellation = new CancellationTokenSource();
        var reader = new ControlledDbDataReader { ThrowOnSynchronousApis = true };
        var manager = CreateManager(reader);
        await using var scope = await manager.BeginAsync();
        await using var command = scope.Connection.CreateCommand();
        await using var overlappingCommand = scope.Connection.CreateCommand();
        await using var wrappedReader = await command.ExecuteReaderAsync();

        var result = await wrappedReader.GetFieldValueAsync<int>(0, cancellation.Token);

        Assert.Equal(37, result);
        Assert.Equal(1, reader.GetFieldValueAsyncCount);
        Assert.Equal(0, reader.LastGetFieldValueOrdinal);
        Assert.Equal(cancellation.Token, reader.LastGetFieldValueCancellationToken);
        await Assert.ThrowsAsync<UnitOfWorkConcurrencyException>(
            () => overlappingCommand.ExecuteScalarAsync());

        await wrappedReader.DisposeAsync();
        await overlappingCommand.ExecuteScalarAsync();
        await scope.RollbackAsync();
    }

    [Fact]
    public async Task IsDBNullAsync_Uses_Provider_Async_API_And_Keeps_Lease()
    {
        using var cancellation = new CancellationTokenSource();
        var reader = new ControlledDbDataReader { ThrowOnSynchronousApis = true };
        var manager = CreateManager(reader);
        await using var scope = await manager.BeginAsync();
        await using var command = scope.Connection.CreateCommand();
        await using var overlappingCommand = scope.Connection.CreateCommand();
        await using var wrappedReader = await command.ExecuteReaderAsync();

        var result = await wrappedReader.IsDBNullAsync(0, cancellation.Token);

        Assert.True(result);
        Assert.Equal(1, reader.IsDBNullAsyncCount);
        Assert.Equal(0, reader.LastIsDBNullOrdinal);
        Assert.Equal(cancellation.Token, reader.LastIsDBNullCancellationToken);
        await Assert.ThrowsAsync<UnitOfWorkConcurrencyException>(
            () => overlappingCommand.ExecuteScalarAsync());

        await wrappedReader.DisposeAsync();
        await overlappingCommand.ExecuteScalarAsync();
        await scope.RollbackAsync();
    }

    [Fact]
    public async Task GetSchemaTableAsync_Uses_Provider_Async_API_And_Keeps_Lease()
    {
        using var cancellation = new CancellationTokenSource();
        var reader = new ControlledDbDataReader { ThrowOnSynchronousApis = true };
        var manager = CreateManager(reader);
        await using var scope = await manager.BeginAsync();
        await using var command = scope.Connection.CreateCommand();
        await using var overlappingCommand = scope.Connection.CreateCommand();
        await using var wrappedReader = await command.ExecuteReaderAsync();

        var result = await wrappedReader.GetSchemaTableAsync(cancellation.Token);

        Assert.Same(reader.SchemaTableAsyncResult, result);
        Assert.Equal(1, reader.GetSchemaTableAsyncCount);
        Assert.Equal(cancellation.Token, reader.LastGetSchemaTableCancellationToken);
        await Assert.ThrowsAsync<UnitOfWorkConcurrencyException>(
            () => overlappingCommand.ExecuteScalarAsync());

        await wrappedReader.DisposeAsync();
        await overlappingCommand.ExecuteScalarAsync();
        await scope.RollbackAsync();
    }

    [Fact]
    public async Task GetColumnSchemaAsync_Uses_Provider_Async_API_And_Keeps_Lease()
    {
        using var cancellation = new CancellationTokenSource();
        var reader = new ControlledDbDataReader { ThrowOnSynchronousApis = true };
        var manager = CreateManager(reader);
        await using var scope = await manager.BeginAsync();
        await using var command = scope.Connection.CreateCommand();
        await using var overlappingCommand = scope.Connection.CreateCommand();
        await using var wrappedReader = await command.ExecuteReaderAsync();

        var result = await wrappedReader.GetColumnSchemaAsync(cancellation.Token);

        Assert.Same(reader.ColumnSchemaAsyncResult, result);
        Assert.Equal(1, reader.GetColumnSchemaAsyncCount);
        Assert.Equal(cancellation.Token, reader.LastGetColumnSchemaCancellationToken);
        await Assert.ThrowsAsync<UnitOfWorkConcurrencyException>(
            () => overlappingCommand.ExecuteScalarAsync());

        await wrappedReader.DisposeAsync();
        await overlappingCommand.ExecuteScalarAsync();
        await scope.RollbackAsync();
    }

    [Fact]
    public async Task Completion_While_Reader_Is_Open_Is_Rejected_Without_Settling_Scope()
    {
        var reader = new ControlledDbDataReader();
        var manager = CreateManager(reader);
        await using var scope = await manager.BeginAsync();
        await using var command = scope.Connection.CreateCommand();
        await using var overlappingCommand = scope.Connection.CreateCommand();

        await using var wrappedReader = await command.ExecuteReaderAsync();

        await Assert.ThrowsAsync<UnitOfWorkConcurrencyException>(() => scope.CompleteAsync());
        await Assert.ThrowsAsync<UnitOfWorkConcurrencyException>(() => overlappingCommand.ExecuteScalarAsync());

        await wrappedReader.DisposeAsync();
        await overlappingCommand.ExecuteScalarAsync();
        await scope.RollbackAsync();
    }

    [Fact]
    public async Task CommandBehavior_CloseConnection_Is_Rejected_Before_Provider_Call()
    {
        ControlledDbCommand? providerCommand = null;
        var connection = new ControlledDbConnection(
            initiallyOpen: true,
            commandFactory: inner => providerCommand = new ControlledDbCommand(
                inner,
                () => 1L,
                executeReader: _ => new ControlledDbDataReader()));
        var manager = CreateManager(connection);
        await using var scope = await manager.BeginAsync();
        await using var command = scope.Connection.CreateCommand();

        Assert.Throws<UnitOfWorkStateException>(() => command.ExecuteReader(CommandBehavior.CloseConnection));

        Assert.NotNull(providerCommand);
        Assert.Equal(0, providerCommand.ExecuteReaderCount);
        await scope.RollbackAsync();
    }

    private static UnitOfWorkManager CreateManager(ControlledDbDataReader reader)
    {
        var connection = new ControlledDbConnection(
            initiallyOpen: true,
            commandFactory: inner => new ControlledDbCommand(
                inner,
                () => 1L,
                executeReader: _ => reader,
                executeReaderAsync: (_, _) => Task.FromResult<DbDataReader>(reader)));
        return CreateManager(connection);
    }

    private static UnitOfWorkManager CreateManager(ControlledDbConnection connection) =>
        new(new ControlledConnectionFactory(connection), (_, _) => throw new NotSupportedException());
}
