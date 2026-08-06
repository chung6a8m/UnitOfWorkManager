using System.Data;
using System.Diagnostics.CodeAnalysis;
using UnitOfWork.Core.Exceptions;

namespace UnitOfWork.Core;

public sealed class TransactionBoundDbConnection : IDbConnection
{
    private readonly IDbConnection _inner;
    private readonly IDbTransaction _transaction;
    private readonly RootUnitOfWork _owner;
    private readonly TransactionBoundDbTransaction _transactionFacade;

    internal TransactionBoundDbConnection(
        IDbConnection inner,
        IDbTransaction transaction,
        RootUnitOfWork owner)
    {
        _inner = inner;
        _transaction = transaction;
        _owner = owner;
        _transactionFacade = new TransactionBoundDbTransaction(transaction, this);
    }

    public IDbCommand CreateCommand()
    {
        _owner.EnsureUsable();
        var command = _inner.CreateCommand();
        command.Transaction = _transaction;
        return new TransactionBoundDbCommand(command, this, _transactionFacade, _owner);
    }

    [AllowNull]
    public string ConnectionString
    {
        get => _inner.ConnectionString;
        set => ThrowOwnershipException();
    }

    public int ConnectionTimeout => _inner.ConnectionTimeout;
    public string Database => _inner.Database;
    public ConnectionState State => _inner.State;

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
