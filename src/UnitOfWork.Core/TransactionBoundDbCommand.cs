using System.Data;
using System.Diagnostics.CodeAnalysis;
using UnitOfWork.Core.Exceptions;

namespace UnitOfWork.Core;

public sealed class TransactionBoundDbCommand : IDbCommand
{
    private readonly IDbCommand _inner;
    private readonly TransactionBoundDbConnection _connection;
    private readonly TransactionBoundDbTransaction _transaction;
    private readonly RootUnitOfWork _owner;

    internal TransactionBoundDbCommand(
        IDbCommand inner,
        TransactionBoundDbConnection connection,
        TransactionBoundDbTransaction transaction,
        RootUnitOfWork owner)
    {
        _inner = inner;
        _connection = connection;
        _transaction = transaction;
        _owner = owner;
    }

    public int ExecuteNonQuery() =>
        _owner.RunGuardedAsync(() => Task.FromResult(_inner.ExecuteNonQuery())).GetAwaiter().GetResult();

    public IDataReader ExecuteReader() =>
        _owner.RunGuardedAsync(() => Task.FromResult(_inner.ExecuteReader())).GetAwaiter().GetResult();

    public IDataReader ExecuteReader(CommandBehavior behavior) =>
        _owner.RunGuardedAsync(() => Task.FromResult(_inner.ExecuteReader(behavior))).GetAwaiter().GetResult();

    public object? ExecuteScalar() =>
        _owner.RunGuardedAsync(() => Task.FromResult(_inner.ExecuteScalar())).GetAwaiter().GetResult();

    [AllowNull]
    public string CommandText
    {
        get => _inner.CommandText;
        set => _inner.CommandText = value;
    }

    public int CommandTimeout
    {
        get => _inner.CommandTimeout;
        set => _inner.CommandTimeout = value;
    }

    public CommandType CommandType
    {
        get => _inner.CommandType;
        set => _inner.CommandType = value;
    }

    public IDbConnection? Connection
    {
        get => _connection;
        set => EnsureBoundResource(value, _connection, "connection");
    }

    public IDataParameterCollection Parameters => _inner.Parameters;

    public IDbTransaction? Transaction
    {
        get => _transaction;
        set => EnsureBoundResource(value, _transaction, "transaction");
    }

    public UpdateRowSource UpdatedRowSource
    {
        get => _inner.UpdatedRowSource;
        set => _inner.UpdatedRowSource = value;
    }

    public void Cancel() => _inner.Cancel();
    public IDbDataParameter CreateParameter() => _inner.CreateParameter();
    public void Prepare() => _inner.Prepare();
    public void Dispose() => _inner.Dispose();

    private static void EnsureBoundResource(object? value, object expected, string resourceName)
    {
        if (!ReferenceEquals(value, expected))
        {
            throw new UnitOfWorkStateException(
                $"A transaction-bound command cannot be assigned a different {resourceName}.");
        }
    }
}
