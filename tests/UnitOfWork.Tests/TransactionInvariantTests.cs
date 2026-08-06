using System.Data;
using System.Data.Common;
using System.Reflection;
using UnitOfWork.Core;
using UnitOfWork.Core.Exceptions;
using UnitOfWork.Tests.Fixtures;
using Xunit;

namespace UnitOfWork.Tests;

public class TransactionInvariantTests
{
    [Fact]
    public async Task CreateCommand_Assigns_Exact_Root_Transaction_To_Controlled_Provider_Command()
    {
        ControlledDbCommand? providerCommand = null;
        var connection = new ControlledDbConnection(
            initiallyOpen: true,
            commandFactory: innerConnection => providerCommand =
                new ControlledDbCommand(innerConnection, () => null));
        var root = CreateRoot(connection);
        using var scope = root.AcquireScope();
        await root.InitializeAsync();

        using var command = scope.Connection.CreateCommand();

        Assert.NotNull(providerCommand);
        Assert.Same(connection.LastTransaction, providerCommand.LastAssignedTransaction);

        await scope.RollbackAsync();
    }

    [Fact]
    public async Task Repository_Command_Without_Manual_Transaction_Is_Rolled_Back()
    {
        using var db = new SqliteTestDb();
        var root = CreateRoot(db.CreateConnection());
        using var scope = root.AcquireScope();
        await root.InitializeAsync();

        var repository = root.GetRepository<TransactionCapturingCounterRepository>();
        repository.Insert(7);
        await scope.RollbackAsync();

        Assert.NotNull(repository.CommandTransaction);
        Assert.NotSame(root.Transaction, repository.CommandTransaction);
        Assert.Equal(0, db.CountRows());
    }

    [Fact]
    public async Task Connection_Facade_Rejects_Resource_Ownership_Operations()
    {
        using var db = new SqliteTestDb();
        var root = CreateRoot(db.CreateConnection());
        using var scope = root.AcquireScope();
        await root.InitializeAsync();

        var connection = scope.Connection;

        Assert.Throws<UnitOfWorkStateException>(() => connection.Open());
        Assert.Throws<UnitOfWorkStateException>(() => connection.Close());
        Assert.Throws<UnitOfWorkStateException>(() => connection.Dispose());
        Assert.Throws<UnitOfWorkStateException>(() => connection.BeginTransaction());
        Assert.Throws<UnitOfWorkStateException>(() => connection.BeginTransaction(IsolationLevel.Serializable));
        Assert.Throws<UnitOfWorkStateException>(() => connection.ChangeDatabase("other"));
        Assert.Throws<UnitOfWorkStateException>(() => connection.ConnectionString = "Data Source=other.db");

        await scope.RollbackAsync();
    }

    [Fact]
    public async Task Connection_Facade_Rejects_OpenAsync()
    {
        using var db = new SqliteTestDb();
        var root = CreateRoot(db.CreateConnection());
        using var scope = root.AcquireScope();
        await root.InitializeAsync();

        DbConnection connection = scope.Connection;

        await Assert.ThrowsAsync<UnitOfWorkStateException>(async () => await connection.OpenAsync());

        await scope.RollbackAsync();
    }

    [Fact]
    public async Task Connection_Facade_Rejects_CloseAsync()
    {
        using var db = new SqliteTestDb();
        var root = CreateRoot(db.CreateConnection());
        using var scope = root.AcquireScope();
        await root.InitializeAsync();

        DbConnection connection = scope.Connection;

        await Assert.ThrowsAsync<UnitOfWorkStateException>(async () => await connection.CloseAsync());

        await scope.RollbackAsync();
    }

    [Fact]
    public async Task Connection_Facade_Rejects_BeginTransactionAsync()
    {
        using var db = new SqliteTestDb();
        var root = CreateRoot(db.CreateConnection());
        using var scope = root.AcquireScope();
        await root.InitializeAsync();

        DbConnection connection = scope.Connection;

        await Assert.ThrowsAsync<UnitOfWorkStateException>(async () => await connection.BeginTransactionAsync());

        await scope.RollbackAsync();
    }

    [Fact]
    public async Task Connection_Facade_Rejects_Isolated_BeginTransactionAsync()
    {
        using var db = new SqliteTestDb();
        var root = CreateRoot(db.CreateConnection());
        using var scope = root.AcquireScope();
        await root.InitializeAsync();

        DbConnection connection = scope.Connection;

        await Assert.ThrowsAsync<UnitOfWorkStateException>(async () => await connection.BeginTransactionAsync(
            IsolationLevel.Serializable));

        await scope.RollbackAsync();
    }

    [Fact]
    public async Task Connection_Facade_Rejects_DisposeAsync()
    {
        using var db = new SqliteTestDb();
        var root = CreateRoot(db.CreateConnection());
        using var scope = root.AcquireScope();
        await root.InitializeAsync();

        DbConnection connection = scope.Connection;

        await Assert.ThrowsAsync<UnitOfWorkStateException>(async () => await connection.DisposeAsync());

        await scope.RollbackAsync();
    }

    [Fact]
    public async Task Connection_Facade_Rejects_Batch_Creation()
    {
        using var db = new SqliteTestDb();
        var root = CreateRoot(db.CreateConnection());
        using var scope = root.AcquireScope();
        await root.InitializeAsync();

        DbConnection connection = scope.Connection;

        Assert.Throws<NotSupportedException>(() => connection.CreateBatch());

        await scope.RollbackAsync();
    }

    [Fact]
    public async Task Command_Rejects_Foreign_Connection_And_Transaction()
    {
        using var db = new SqliteTestDb();
        var root = CreateRoot(db.CreateConnection());
        using var scope = root.AcquireScope();
        await root.InitializeAsync();
        using var foreignConnection = db.CreateConnection();
        using var command = scope.Connection.CreateCommand();
        var foreignTransaction = new ForeignTransaction();

        Assert.Throws<UnitOfWorkStateException>(() => command.Connection = null);
        Assert.Throws<UnitOfWorkStateException>(() => command.Connection = foreignConnection);
        Assert.Throws<UnitOfWorkStateException>(() => command.Transaction = null);
        Assert.Throws<UnitOfWorkStateException>(() => command.Transaction = foreignTransaction);

        await scope.RollbackAsync();
    }

    [Fact]
    public async Task Transaction_Metadata_Cannot_Control_Root_Transaction()
    {
        using var db = new SqliteTestDb();
        var root = CreateRoot(db.CreateConnection());
        using var scope = root.AcquireScope();
        await root.InitializeAsync();
        using var command = scope.Connection.CreateCommand();
        var transaction = command.Transaction;

        Assert.NotNull(transaction);
        Assert.NotSame(root.Transaction, transaction);
        Assert.Throws<UnitOfWorkStateException>(() => transaction.Commit());
        Assert.Throws<UnitOfWorkStateException>(() => transaction.Rollback());
        Assert.Throws<UnitOfWorkStateException>(() => transaction.Dispose());

        await scope.RollbackAsync();
    }

    [Fact]
    public async Task Raw_Connection_Is_Not_Exposed_Through_Public_Context()
    {
        using var db = new SqliteTestDb();
        var rawConnection = db.CreateConnection();
        var root = CreateRoot(rawConnection);
        using var scope = root.AcquireScope();
        await root.InitializeAsync();

        Assert.NotSame(rawConnection, scope.Connection);

        await scope.RollbackAsync();
    }

    [Fact]
    public async Task Facades_Do_Not_Retain_Raw_Root_Resources()
    {
        using var db = new SqliteTestDb();
        var rawConnection = db.CreateConnection();
        var root = CreateRoot(rawConnection);
        using var scope = root.AcquireScope();
        await root.InitializeAsync();
        using var command = scope.Connection.CreateCommand();

        var connectionFacade = Assert.IsType<TransactionBoundDbConnection>(scope.Connection);
        var transactionFacade = Assert.IsType<TransactionBoundDbTransaction>(command.Transaction);

        AssertDoesNotRetainRawResource(connectionFacade, rawConnection);
        AssertDoesNotRetainRawResource(connectionFacade, root.Transaction!);
        AssertDoesNotRetainRawResource(transactionFacade, rawConnection);
        AssertDoesNotRetainRawResource(transactionFacade, root.Transaction!);

        await scope.RollbackAsync();
    }

    private static RootUnitOfWork CreateRoot(DbConnection connection) =>
        new(
            connection,
            (type, boundConnection) => type == typeof(TransactionCapturingCounterRepository)
                ? new TransactionCapturingCounterRepository(boundConnection)
                : throw new NotSupportedException(),
            () => true,
            () => { });

    private sealed class TransactionCapturingCounterRepository
    {
        private readonly DbConnection _connection;

        public TransactionCapturingCounterRepository(DbConnection connection) => _connection = connection;

        public DbTransaction? CommandTransaction { get; private set; }

        public void Insert(int value)
        {
            using var command = _connection.CreateCommand();
            CommandTransaction = command.Transaction;
            command.CommandText = "INSERT INTO Counter (Value) VALUES ($value);";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$value";
            parameter.Value = value;
            command.Parameters.Add(parameter);
            command.ExecuteNonQuery();
        }
    }

    private sealed class ForeignTransaction : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection? DbConnection => null;

        public override void Commit() { }
        public override void Rollback() { }
    }

    private static void AssertDoesNotRetainRawResource(object facade, object rawResource)
    {
        var retainedValues = facade.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(field => field.GetValue(facade));

        Assert.DoesNotContain(retainedValues, value => ReferenceEquals(value, rawResource));
    }
}
