using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using UnitOfWork.Core.Exceptions;

namespace UnitOfWork.Core;

public sealed class TransactionBoundDbCommand : DbCommand
{
    private readonly DbCommand _inner;
    private readonly TransactionBoundDbConnection _connection;
    private readonly TransactionBoundDbTransaction _transaction;
    private readonly RootUnitOfWork _owner;

    internal TransactionBoundDbCommand(
        DbCommand inner,
        TransactionBoundDbConnection connection,
        TransactionBoundDbTransaction transaction,
        RootUnitOfWork owner)
    {
        _inner = inner;
        _connection = connection;
        _transaction = transaction;
        _owner = owner;
    }

    [AllowNull]
    public override string CommandText
    {
        get => _inner.CommandText;
        set => _inner.CommandText = value;
    }

    public override int CommandTimeout
    {
        get => _inner.CommandTimeout;
        set => _inner.CommandTimeout = value;
    }

    public override CommandType CommandType
    {
        get => _inner.CommandType;
        set => _inner.CommandType = value;
    }

    public override bool DesignTimeVisible
    {
        get => _inner.DesignTimeVisible;
        set => _inner.DesignTimeVisible = value;
    }

    protected override DbConnection? DbConnection
    {
        get => _connection;
        set => EnsureBoundResource(value, _connection, "connection");
    }

    protected override DbParameterCollection DbParameterCollection => _inner.Parameters;

    protected override DbTransaction? DbTransaction
    {
        get => _transaction;
        set => EnsureBoundResource(value, _transaction, "transaction");
    }

    public override UpdateRowSource UpdatedRowSource
    {
        get => _inner.UpdatedRowSource;
        set => _inner.UpdatedRowSource = value;
    }

    public override void Cancel() => _inner.Cancel();

    public override int ExecuteNonQuery()
    {
        using var lease = _owner.EnterOperation(nameof(ExecuteNonQuery));
        return _inner.ExecuteNonQuery();
    }

    public override object? ExecuteScalar()
    {
        using var lease = _owner.EnterOperation(nameof(ExecuteScalar));
        return _inner.ExecuteScalar();
    }

    public override void Prepare()
    {
        using var lease = _owner.EnterOperation(nameof(Prepare));
        _inner.Prepare();
    }

    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        using var lease = _owner.EnterOperation(nameof(ExecuteNonQueryAsync));
        return await _inner.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        using var lease = _owner.EnterOperation(nameof(ExecuteScalarAsync));
        return await _inner.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        using var lease = _owner.EnterOperation(nameof(PrepareAsync));
        await _inner.PrepareAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override DbParameter CreateDbParameter() => _inner.CreateParameter();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        using var lease = _owner.EnterOperation(nameof(ExecuteDbDataReader));
        return _inner.ExecuteReader(behavior);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();

        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => _inner.DisposeAsync();

    private static void EnsureBoundResource(object? value, object expected, string resourceName)
    {
        if (!ReferenceEquals(value, expected))
        {
            throw new UnitOfWorkStateException(
                $"A transaction-bound command cannot be assigned a different {resourceName}.");
        }
    }
}
