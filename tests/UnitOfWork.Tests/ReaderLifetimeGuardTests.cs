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
