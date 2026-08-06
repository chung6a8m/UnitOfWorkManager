using System.Data;
using System.Data.Common;
using UnitOfWork.Core;
using UnitOfWork.Core.Exceptions;
using UnitOfWork.Tests.Fixtures;
using Xunit;

namespace UnitOfWork.Tests;

public sealed class UnitOfWorkOptionsTests
{
    [Fact]
    public void Public_Context_Exposes_DbConnection_Facade()
    {
        Assert.Equal(typeof(DbConnection), typeof(IUnitOfWorkContext)
            .GetProperty(nameof(IUnitOfWorkContext.Connection))!.PropertyType);
    }

    [Fact]
    public void Connection_Factory_Returns_DbConnection()
    {
        Assert.Equal(typeof(DbConnection), typeof(IDbConnectionFactory)
            .GetMethod(nameof(IDbConnectionFactory.CreateConnection))!.ReturnType);
    }

    [Fact]
    public async Task BeginAsync_Uses_Requested_Isolation_Level()
    {
        var connection = new ControlledDbConnection(initiallyOpen: true);
        var manager = CreateManager(connection);

        await using var scope = await manager.BeginAsync(new UnitOfWorkOptions
        {
            IsolationLevel = IsolationLevel.Serializable
        });

        Assert.Equal(IsolationLevel.Serializable, connection.LastTransaction!.IsolationLevel);
        await scope.RollbackAsync();
    }

    [Fact]
    public async Task Command_Timeout_Option_Is_Applied_To_Every_New_Command()
    {
        var createdCommands = new List<ControlledDbCommand>();
        var connection = new ControlledDbConnection(
            initiallyOpen: true,
            commandFactory: innerConnection =>
            {
                var command = new ControlledDbCommand(innerConnection, () => null);
                createdCommands.Add(command);
                return command;
            });
        var manager = CreateManager(connection);

        await using var scope = await manager.BeginAsync(new UnitOfWorkOptions
        {
            CommandTimeoutSeconds = 15
        });
        using var first = scope.Connection.CreateCommand();
        using var second = scope.Connection.CreateCommand();

        Assert.Equal(new[] { 15, 15 }, createdCommands.Select(command => command.CommandTimeout));
        await scope.RollbackAsync();
    }

    [Fact]
    public async Task Nested_Begin_With_Different_Options_Is_Rejected_Before_Acquiring_Scope()
    {
        var connection = new ControlledDbConnection(initiallyOpen: true);
        var manager = CreateManager(connection);
        var outerOptions = new UnitOfWorkOptions
        {
            IsolationLevel = IsolationLevel.Serializable,
            CommandTimeoutSeconds = 15
        };

        await using var outer = await manager.BeginAsync(outerOptions);

        await Assert.ThrowsAsync<UnitOfWorkStateException>(() => manager.BeginAsync(
            outerOptions with { CommandTimeoutSeconds = 30 }));

        Assert.Equal(1, GetRoot(manager).ActiveScopeCount);
        await outer.RollbackAsync();
    }

    [Fact]
    public async Task Invalid_Command_Timeout_Is_Rejected_Before_Connection_Creation()
    {
        var factory = new ControlledConnectionFactory(new ControlledDbConnection(initiallyOpen: true));
        var manager = CreateManager(factory);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => manager.BeginAsync(new UnitOfWorkOptions
        {
            CommandTimeoutSeconds = 0
        }));

        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public async Task Invalid_Transaction_Timeout_Is_Rejected_Before_Connection_Creation()
    {
        var factory = new ControlledConnectionFactory(new ControlledDbConnection(initiallyOpen: true));
        var manager = CreateManager(factory);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => manager.BeginAsync(new UnitOfWorkOptions
        {
            TransactionTimeout = TimeSpan.Zero
        }));

        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public async Task Default_Transaction_Factory_Rejects_ReadOnly_And_TransactionTimeout()
    {
        var factory = new DefaultUnitOfWorkTransactionFactory();
        var connection = new ControlledDbConnection(initiallyOpen: true);

        await Assert.ThrowsAsync<NotSupportedException>(async () => await factory.BeginTransactionAsync(
            connection,
            new UnitOfWorkOptions { ReadOnly = true },
            CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await factory.BeginTransactionAsync(
            connection,
            new UnitOfWorkOptions { TransactionTimeout = TimeSpan.FromSeconds(1) },
            CancellationToken.None));
    }

    [Fact]
    public async Task Custom_Transaction_Factory_Receives_Full_Options()
    {
        var connection = new ControlledDbConnection(initiallyOpen: true);
        var transactionFactory = new CapturingTransactionFactory();
        var manager = CreateManager(connection, transactionFactory);
        var options = new UnitOfWorkOptions
        {
            IsolationLevel = IsolationLevel.RepeatableRead,
            CommandTimeoutSeconds = 45,
            TransactionTimeout = TimeSpan.FromSeconds(30),
            ReadOnly = true
        };

        await using var scope = await manager.BeginAsync(options);

        Assert.Same(connection, transactionFactory.Connection);
        Assert.Equal(options, transactionFactory.Options);
        await scope.RollbackAsync();
    }

    private static UnitOfWorkManager CreateManager(
        ControlledDbConnection connection,
        IUnitOfWorkTransactionFactory? transactionFactory = null) =>
        new(
            new ControlledConnectionFactory(connection),
            (_, _) => throw new NotSupportedException(),
            transactionFactory);

    private static UnitOfWorkManager CreateManager(ControlledConnectionFactory factory) =>
        new(factory, (_, _) => throw new NotSupportedException());

    private static RootUnitOfWork GetRoot(UnitOfWorkManager manager) =>
        Assert.IsType<RootUnitOfWork>(manager.Current);

    private sealed class CapturingTransactionFactory : IUnitOfWorkTransactionFactory
    {
        public DbConnection? Connection { get; private set; }
        public UnitOfWorkOptions? Options { get; private set; }

        public ValueTask<DbTransaction> BeginTransactionAsync(
            DbConnection connection,
            UnitOfWorkOptions options,
            CancellationToken cancellationToken)
        {
            Connection = connection;
            Options = options;
            return connection.BeginTransactionAsync(
                options.IsolationLevel ?? IsolationLevel.ReadCommitted,
                cancellationToken);
        }
    }
}
