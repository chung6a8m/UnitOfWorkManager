using System.Data;
using UnitOfWork.Core.Exceptions;

namespace UnitOfWork.Core;

public sealed class TransactionBoundDbTransaction : IDbTransaction
{
    private readonly RootUnitOfWork _owner;
    private readonly TransactionBoundDbConnection _connection;

    internal TransactionBoundDbTransaction(RootUnitOfWork owner, TransactionBoundDbConnection connection)
    {
        _owner = owner;
        _connection = connection;
    }

    public IDbConnection Connection => _connection;
    public IsolationLevel IsolationLevel => _owner.GetTransactionIsolationLevel();

    public void Commit() => ThrowOwnershipException();
    public void Rollback() => ThrowOwnershipException();
    public void Dispose() => ThrowOwnershipException();

    private static void ThrowOwnershipException() => throw new UnitOfWorkStateException(
        "The root unit of work exclusively controls its transaction lifecycle.");
}
