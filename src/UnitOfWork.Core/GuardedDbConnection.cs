using System.Data;

namespace UnitOfWork.Core;

/// <summary>
/// Bọc IDbConnection thật — mọi IDbCommand tạo ra từ đây đều đi qua GuardedDbCommand,
/// nhờ đó Dapper (chạy qua Connection.CreateCommand()) cũng được guard đồng thời/flow
/// mà không cần sửa code Repository.
/// </summary>
public sealed class GuardedDbConnection : IDbConnection
{
    private readonly IDbConnection _inner;
    private readonly UnitOfWork _owner;

    public GuardedDbConnection(IDbConnection inner, UnitOfWork owner)
    {
        _inner = inner;
        _owner = owner;
    }

    public IDbCommand CreateCommand()
    {
        var innerCommand = _inner.CreateCommand();
        return new GuardedDbCommand(innerCommand, _owner);
    }

    public string ConnectionString
    {
        get => _inner.ConnectionString;
        set => _inner.ConnectionString = value;
    }

    public int ConnectionTimeout => _inner.ConnectionTimeout;
    public string Database => _inner.Database;
    public ConnectionState State => _inner.State;

    public IDbTransaction BeginTransaction() => _inner.BeginTransaction();
    public IDbTransaction BeginTransaction(IsolationLevel il) => _inner.BeginTransaction(il);
    public void ChangeDatabase(string databaseName) => _inner.ChangeDatabase(databaseName);
    public void Close() => _inner.Close();
    public void Open() => _inner.Open();
    public void Dispose() => _inner.Dispose();
}
