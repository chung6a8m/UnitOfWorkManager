using System.Data;
using UnitOfWork.Core.Exceptions;

namespace UnitOfWork.Core;

public sealed class TransactionBoundDbTransaction : IDbTransaction
{
    private readonly IDbTransaction _inner;
    private readonly IDbConnection _connection;

    internal TransactionBoundDbTransaction(IDbTransaction inner, IDbConnection connection)
    {
        _inner = inner;
        _connection = connection;
    }

    public IDbConnection Connection => _connection;
    public IsolationLevel IsolationLevel => _inner.IsolationLevel;

    public void Commit() => ThrowOwnershipException();
    public void Rollback() => ThrowOwnershipException();
    public void Dispose() => ThrowOwnershipException();

    private static void ThrowOwnershipException() => throw new UnitOfWorkStateException(
        "The root unit of work exclusively controls its transaction lifecycle.");
}
