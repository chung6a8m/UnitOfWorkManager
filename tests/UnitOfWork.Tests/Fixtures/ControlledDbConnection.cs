using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using UnitOfWork.Core;

namespace UnitOfWork.Tests.Fixtures;

internal sealed class ControlledConnectionFactory : IDbConnectionFactory
{
    private readonly Queue<ControlledDbConnection> _connections;

    public ControlledConnectionFactory(params ControlledDbConnection[] connections)
    {
        _connections = new Queue<ControlledDbConnection>(connections);
    }

    public int CreateCount { get; private set; }

    public IDbConnection CreateConnection()
    {
        CreateCount++;
        return _connections.Dequeue();
    }
}

internal sealed class ControlledDbConnection : DbConnection
{
    private readonly TaskCompletionSource _openGate =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Exception? _beginTransactionException;
    private readonly Exception? _disposeException;
    private readonly Exception? _transactionDisposeException;
    private readonly Exception? _commitException;
    private readonly Exception? _rollbackException;
    private ConnectionState _state;

    public ControlledDbConnection(
        bool initiallyOpen = false,
        Exception? beginTransactionException = null,
        Exception? disposeException = null,
        Exception? transactionDisposeException = null,
        Exception? commitException = null,
        Exception? rollbackException = null)
    {
        _state = initiallyOpen ? ConnectionState.Open : ConnectionState.Closed;
        _beginTransactionException = beginTransactionException;
        _disposeException = disposeException;
        _transactionDisposeException = transactionDisposeException;
        _commitException = commitException;
        _rollbackException = rollbackException;

        if (initiallyOpen)
            _openGate.TrySetResult();
    }

    public bool IsDisposed { get; private set; }
    public ControlledDbTransaction? LastTransaction { get; private set; }
    public Action? Opening { get; set; }

    public void ReleaseOpen() => _openGate.TrySetResult();

    [AllowNull]
    public override string ConnectionString { get; set; } = string.Empty;
    public override string Database => "Controlled";
    public override string DataSource => "Controlled";
    public override string ServerVersion => "1.0";
    public override ConnectionState State => _state;

    public override void ChangeDatabase(string databaseName) { }

    public override void Close() => _state = ConnectionState.Closed;

    public override void Open() => _state = ConnectionState.Open;

    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
        Opening?.Invoke();
        await _openGate.Task.WaitAsync(cancellationToken);
        _state = ConnectionState.Open;
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        if (_beginTransactionException is not null)
            throw _beginTransactionException;

        LastTransaction = new ControlledDbTransaction(
            this,
            isolationLevel,
            _transactionDisposeException,
            _commitException,
            _rollbackException);
        return LastTransaction;
    }

    protected override DbCommand CreateDbCommand() =>
        throw new NotSupportedException("Tests never execute commands on this connection.");

    protected override void Dispose(bool disposing)
    {
        IsDisposed = true;
        _state = ConnectionState.Closed;
        base.Dispose(disposing);

        if (disposing && _disposeException is not null)
            throw _disposeException;
    }
}

internal sealed class ControlledDbTransaction : DbTransaction
{
    private readonly DbConnection _connection;
    private readonly IsolationLevel _isolationLevel;
    private readonly Exception? _disposeException;
    private readonly Exception? _commitException;
    private readonly Exception? _rollbackException;

    public ControlledDbTransaction(
        DbConnection connection,
        IsolationLevel isolationLevel,
        Exception? disposeException,
        Exception? commitException,
        Exception? rollbackException)
    {
        _connection = connection;
        _isolationLevel = isolationLevel;
        _disposeException = disposeException;
        _commitException = commitException;
        _rollbackException = rollbackException;
    }

    public int CommitCount { get; private set; }
    public int RollbackCount { get; private set; }
    public int DisposeCount { get; private set; }

    public override IsolationLevel IsolationLevel => _isolationLevel;
    protected override DbConnection DbConnection => _connection;

    public override void Commit()
    {
        CommitCount++;
        if (_commitException is not null)
            throw _commitException;
    }

    public override void Rollback()
    {
        RollbackCount++;
        if (_rollbackException is not null)
            throw _rollbackException;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            DisposeCount++;

        base.Dispose(disposing);

        if (disposing && _disposeException is not null)
            throw _disposeException;
    }
}
