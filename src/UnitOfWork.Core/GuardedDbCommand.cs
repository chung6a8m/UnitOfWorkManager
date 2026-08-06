using System.Data;
using System.Diagnostics.CodeAnalysis;

namespace UnitOfWork.Core;

/// <summary>
/// Mọi Execute*/ExecuteReader đều chạy qua UnitOfWork.RunGuardedAsync trước khi
/// chạm tới DB thật — nhờ đó truy cập trái phép (sai flow, hoặc 2 thao tác chạy
/// đồng thời trên cùng UoW) bị chặn ngay tại tầng thấp nhất, không phụ thuộc
/// Repository có tự gọi guard hay không.
/// </summary>
public sealed class GuardedDbCommand : IDbCommand
{
    private readonly IDbCommand _inner;
    private readonly UnitOfWork _owner;

    public GuardedDbCommand(IDbCommand inner, UnitOfWork owner)
    {
        _inner = inner;
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
        get => _inner.Connection;
        set => _inner.Connection = value;
    }

    public IDataParameterCollection Parameters => _inner.Parameters;

    public IDbTransaction? Transaction
    {
        get => _inner.Transaction;
        set => _inner.Transaction = value;
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
}
