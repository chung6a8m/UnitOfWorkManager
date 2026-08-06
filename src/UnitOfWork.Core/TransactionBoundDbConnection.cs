using System.Data;
using System.Diagnostics.CodeAnalysis;
using UnitOfWork.Core.Exceptions;

namespace UnitOfWork.Core;

public sealed class TransactionBoundDbConnection : IDbConnection
{
    private readonly RootUnitOfWork _owner;
    private readonly TransactionBoundDbTransaction _transactionFacade;

    internal TransactionBoundDbConnection(RootUnitOfWork owner)
    {
        _owner = owner;
        _transactionFacade = new TransactionBoundDbTransaction(owner, this);
    }

    public IDbCommand CreateCommand()
    {
        var command = _owner.CreateTransactionBoundCommand();
        return new TransactionBoundDbCommand(command, this, _transactionFacade, _owner);
    }

    [AllowNull]
    public string ConnectionString
    {
        get => _owner.GetConnectionString();
        set => ThrowOwnershipException();
    }

    public int ConnectionTimeout => _owner.GetConnectionTimeout();
    public string Database => _owner.GetDatabase();
    public ConnectionState State => _owner.GetConnectionState();

    public IDbTransaction BeginTransaction() => ThrowOwnershipException<IDbTransaction>();
    public IDbTransaction BeginTransaction(IsolationLevel il) => ThrowOwnershipException<IDbTransaction>();
    public void ChangeDatabase(string databaseName) => ThrowOwnershipException();
    public void Close() => ThrowOwnershipException();
    public void Open() => ThrowOwnershipException();
    public void Dispose() => ThrowOwnershipException();

    private static void ThrowOwnershipException() => throw new UnitOfWorkStateException(
        "The root unit of work exclusively controls connection ownership operations.");

    private static T ThrowOwnershipException<T>()
    {
        ThrowOwnershipException();
        return default!;
    }
}
