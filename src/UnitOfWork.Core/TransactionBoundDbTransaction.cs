using System.Data;
using System.Data.Common;
using UnitOfWork.Core.Exceptions;

namespace UnitOfWork.Core;

public sealed class TransactionBoundDbTransaction : DbTransaction
{
    private readonly RootUnitOfWork _owner;
    private readonly TransactionBoundDbConnection _connection;

    internal TransactionBoundDbTransaction(RootUnitOfWork owner, TransactionBoundDbConnection connection)
    {
        _owner = owner;
        _connection = connection;
    }

    public override IsolationLevel IsolationLevel => _owner.GetTransactionIsolationLevel();

    protected override DbConnection DbConnection => _connection;

    public override void Commit() => ThrowOwnershipException();
    public override void Rollback() => ThrowOwnershipException();
    protected override void Dispose(bool disposing) => ThrowOwnershipException();

    private static void ThrowOwnershipException() => throw new UnitOfWorkStateException(
        "The root unit of work exclusively controls its transaction lifecycle.");
}
