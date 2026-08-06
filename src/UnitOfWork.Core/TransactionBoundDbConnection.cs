using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using UnitOfWork.Core.Exceptions;

namespace UnitOfWork.Core;

public sealed class TransactionBoundDbConnection : DbConnection
{
    private readonly RootUnitOfWork _owner;
    private readonly TransactionBoundDbTransaction _transactionFacade;

    internal TransactionBoundDbConnection(RootUnitOfWork owner)
    {
        _owner = owner;
        _transactionFacade = new TransactionBoundDbTransaction(owner, this);
    }

    [AllowNull]
    public override string ConnectionString
    {
        get => _owner.GetConnectionString();
        set => ThrowOwnershipException();
    }

    public override int ConnectionTimeout => _owner.GetConnectionTimeout();
    public override string Database => _owner.GetDatabase();
    public override string DataSource => _owner.GetDataSource();
    public override string ServerVersion => _owner.GetServerVersion();
    public override ConnectionState State => _owner.GetConnectionState();

    public override void ChangeDatabase(string databaseName) => ThrowOwnershipException();
    public override void Close() => ThrowOwnershipException();
    public override Task CloseAsync() => ThrowOwnershipException<Task>();
    public override void Open() => ThrowOwnershipException();
    public override Task OpenAsync(CancellationToken cancellationToken) => ThrowOwnershipException<Task>();
    public override ValueTask DisposeAsync() => ThrowOwnershipException<ValueTask>();

    protected override ValueTask<DbTransaction> BeginDbTransactionAsync(
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken) => ThrowOwnershipException<ValueTask<DbTransaction>>();

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        ThrowOwnershipException<DbTransaction>();

    protected override DbCommand CreateDbCommand()
    {
        var command = _owner.CreateTransactionBoundCommand();
        return new TransactionBoundDbCommand(command, this, _transactionFacade, _owner);
    }

    protected override DbBatch CreateDbBatch() => ThrowBatchException<DbBatch>();

    protected override void Dispose(bool disposing) => ThrowOwnershipException();

    private static void ThrowOwnershipException() => throw new UnitOfWorkStateException(
        "The root unit of work exclusively controls connection ownership operations.");

    private static T ThrowOwnershipException<T>()
    {
        ThrowOwnershipException();
        return default!;
    }

    private static T ThrowBatchException<T>() => throw new NotSupportedException(
        "Transaction-bound batch commands are not supported.");
}
