using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using UnitOfWork.Core;

namespace UnitOfWork.Tests.Fixtures;

internal sealed class ControlledConnectionFactory : IDbConnectionFactory
{
    private readonly Queue<DbConnection> _connections;

    public ControlledConnectionFactory(params DbConnection[] connections)
    {
        _connections = new Queue<DbConnection>(connections);
    }

    public int CreateCount { get; private set; }

    public DbConnection CreateConnection()
    {
        CreateCount++;
        return _connections.Dequeue();
    }
}

internal sealed class ControlledDbConnection : DbConnection
{
    private readonly TaskCompletionSource _openGate =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _beginTransactionAsyncGate =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _connectionDisposeAsyncGate =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Exception? _beginTransactionException;
    private readonly Exception? _openAsyncException;
    private readonly Exception? _beginTransactionAsyncException;
    private readonly Exception? _disposeException;
    private readonly Exception? _connectionDisposeAsyncException;
    private readonly Exception? _transactionDisposeException;
    private readonly Exception? _transactionDisposeAsyncException;
    private readonly Exception? _commitException;
    private readonly Exception? _commitAsyncException;
    private readonly Exception? _rollbackException;
    private readonly Exception? _rollbackAsyncException;
    private readonly bool _blockCommitAsync;
    private readonly bool _blockRollbackAsync;
    private readonly bool _blockTransactionDisposeAsync;
    private readonly Func<DbConnection, DbCommand>? _commandFactory;
    private ConnectionState _state;

    public ControlledDbConnection(
        bool initiallyOpen = false,
        Exception? beginTransactionException = null,
        Exception? disposeException = null,
        Exception? transactionDisposeException = null,
        Exception? commitException = null,
        Exception? rollbackException = null,
        Func<DbConnection, DbCommand>? commandFactory = null,
        Exception? openAsyncException = null,
        Exception? beginTransactionAsyncException = null,
        Exception? connectionDisposeAsyncException = null,
        Exception? transactionDisposeAsyncException = null,
        Exception? commitAsyncException = null,
        Exception? rollbackAsyncException = null,
        bool blockBeginTransactionAsync = false,
        bool blockCommitAsync = false,
        bool blockRollbackAsync = false,
        bool blockTransactionDisposeAsync = false,
        bool blockConnectionDisposeAsync = false)
    {
        _state = initiallyOpen ? ConnectionState.Open : ConnectionState.Closed;
        _beginTransactionException = beginTransactionException;
        _openAsyncException = openAsyncException;
        _beginTransactionAsyncException = beginTransactionAsyncException ?? beginTransactionException;
        _disposeException = disposeException;
        _connectionDisposeAsyncException = connectionDisposeAsyncException ?? disposeException;
        _transactionDisposeException = transactionDisposeException;
        _transactionDisposeAsyncException =
            transactionDisposeAsyncException ?? transactionDisposeException;
        _commitException = commitException;
        _commitAsyncException = commitAsyncException ?? commitException;
        _rollbackException = rollbackException;
        _rollbackAsyncException = rollbackAsyncException ?? rollbackException;
        _blockCommitAsync = blockCommitAsync;
        _blockRollbackAsync = blockRollbackAsync;
        _blockTransactionDisposeAsync = blockTransactionDisposeAsync;
        _commandFactory = commandFactory;

        if (initiallyOpen)
            _openGate.TrySetResult();

        if (!blockBeginTransactionAsync)
            _beginTransactionAsyncGate.TrySetResult();

        if (!blockConnectionDisposeAsync)
            _connectionDisposeAsyncGate.TrySetResult();
    }

    public bool IsDisposed { get; private set; }
    public ControlledDbTransaction? LastTransaction { get; private set; }
    public Action? Opening { get; set; }
    public int OpenCount { get; private set; }
    public int OpenAsyncCount { get; private set; }
    public int BeginTransactionCount { get; private set; }
    public int BeginTransactionAsyncCount { get; private set; }
    public int CommitAsyncCount => LastTransaction?.CommitAsyncCount ?? 0;
    public int RollbackAsyncCount => LastTransaction?.RollbackAsyncCount ?? 0;
    public int TransactionDisposeAsyncCount =>
        LastTransaction?.TransactionDisposeAsyncCount ?? 0;
    public int ConnectionDisposeCount { get; private set; }
    public int ConnectionDisposeAsyncCount { get; private set; }
    public CancellationToken LastOpenCancellationToken { get; private set; }
    public CancellationToken LastBeginCancellationToken { get; private set; }
    public CancellationToken LastCommitCancellationToken =>
        LastTransaction?.LastCommitCancellationToken ?? default;
    public CancellationToken LastRollbackCancellationToken =>
        LastTransaction?.LastRollbackCancellationToken ?? default;
    public TaskCompletionSource OpenAsyncStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource BeginTransactionAsyncStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource CommitAsyncStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource RollbackAsyncStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource TransactionDisposeAsyncStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ConnectionDisposeAsyncStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void ReleaseOpen() => _openGate.TrySetResult();
    public void ReleaseBeginTransactionAsync() => _beginTransactionAsyncGate.TrySetResult();
    public void ReleaseCommitAsync() => LastTransaction?.ReleaseCommitAsync();
    public void ReleaseRollbackAsync() => LastTransaction?.ReleaseRollbackAsync();
    public void ReleaseTransactionDisposeAsync() => LastTransaction?.ReleaseDisposeAsync();
    public void ReleaseConnectionDisposeAsync() => _connectionDisposeAsyncGate.TrySetResult();

    [AllowNull]
    public override string ConnectionString { get; set; } = string.Empty;
    public override string Database => "Controlled";
    public override string DataSource => "Controlled";
    public override string ServerVersion => "1.0";
    public override ConnectionState State => _state;

    public override void ChangeDatabase(string databaseName) { }

    public override void Close() => _state = ConnectionState.Closed;

    public override void Open()
    {
        OpenCount++;
        _state = ConnectionState.Open;
    }

    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
        OpenAsyncCount++;
        LastOpenCancellationToken = cancellationToken;
        OpenAsyncStarted.TrySetResult();
        Opening?.Invoke();
        await _openGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        if (_openAsyncException is not null)
            throw _openAsyncException;

        _state = ConnectionState.Open;
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        BeginTransactionCount++;

        if (_beginTransactionException is not null)
            throw _beginTransactionException;

        LastTransaction = CreateTransaction(isolationLevel);
        return LastTransaction;
    }

    protected override async ValueTask<DbTransaction> BeginDbTransactionAsync(
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken)
    {
        BeginTransactionAsyncCount++;
        LastBeginCancellationToken = cancellationToken;
        BeginTransactionAsyncStarted.TrySetResult();
        await _beginTransactionAsyncGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        if (_beginTransactionAsyncException is not null)
            throw _beginTransactionAsyncException;

        LastTransaction = CreateTransaction(isolationLevel);
        return LastTransaction;
    }

    private ControlledDbTransaction CreateTransaction(IsolationLevel isolationLevel) =>
        new(
            this,
            isolationLevel,
            _transactionDisposeException,
            _transactionDisposeAsyncException,
            _commitException,
            _commitAsyncException,
            _rollbackException,
            _rollbackAsyncException,
            _blockCommitAsync,
            _blockRollbackAsync,
            _blockTransactionDisposeAsync,
            CommitAsyncStarted,
            RollbackAsyncStarted,
            TransactionDisposeAsyncStarted);

    protected override DbCommand CreateDbCommand() => _commandFactory?.Invoke(this)
        ?? throw new NotSupportedException("Tests never execute commands on this connection.");

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ConnectionDisposeCount++;
            IsDisposed = true;
            _state = ConnectionState.Closed;
        }

        base.Dispose(disposing);

        if (disposing && _disposeException is not null)
            throw _disposeException;
    }

    public override async ValueTask DisposeAsync()
    {
        ConnectionDisposeAsyncCount++;
        ConnectionDisposeAsyncStarted.TrySetResult();
        await _connectionDisposeAsyncGate.Task.ConfigureAwait(false);
        IsDisposed = true;
        _state = ConnectionState.Closed;

        if (_connectionDisposeAsyncException is not null)
            throw _connectionDisposeAsyncException;
    }
}

internal sealed class ControlledDbCommand : DbCommand
{
    private readonly Func<object?> _executeScalar;
    private readonly Action _prepare;
    private readonly Func<CommandBehavior, DbDataReader>? _executeReader;
    private readonly Func<CommandBehavior, CancellationToken, Task<DbDataReader>>? _executeReaderAsync;
    private DbConnection? _connection;
    private DbTransaction? _transaction;

    public ControlledDbCommand(
        DbConnection connection,
        Func<object?> executeScalar,
        Action? prepare = null,
        Func<CommandBehavior, DbDataReader>? executeReader = null,
        Func<CommandBehavior, CancellationToken, Task<DbDataReader>>? executeReaderAsync = null)
    {
        _connection = connection;
        _executeScalar = executeScalar;
        _prepare = prepare ?? (() => { });
        _executeReader = executeReader;
        _executeReaderAsync = executeReaderAsync;
    }

    public DbTransaction? LastAssignedTransaction { get; private set; }
    public int ExecuteReaderCount { get; private set; }
    public int ExecuteReaderAsyncCount { get; private set; }
    public CommandBehavior? LastReaderBehavior { get; private set; }
    public CancellationToken LastReaderCancellationToken { get; private set; }

    [AllowNull]
    public override string CommandText { get; set; } = string.Empty;
    public override int CommandTimeout { get; set; }
    public override CommandType CommandType { get; set; }
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }

    protected override DbConnection? DbConnection
    {
        get => _connection;
        set => _connection = value;
    }

    protected override DbParameterCollection DbParameterCollection =>
        throw new NotSupportedException("Parameters are not needed by controlled command tests.");

    protected override DbTransaction? DbTransaction
    {
        get => _transaction;
        set
        {
            _transaction = value;
            LastAssignedTransaction = value;
        }
    }

    public override void Cancel() { }
    public override int ExecuteNonQuery() => Convert.ToInt32(_executeScalar());
    public override object? ExecuteScalar() => _executeScalar();
    public override void Prepare() => _prepare();

    protected override DbParameter CreateDbParameter() =>
        throw new NotSupportedException("Parameters are not needed by controlled command tests.");

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        ExecuteReaderCount++;
        LastReaderBehavior = behavior;
        return _executeReader?.Invoke(behavior)
            ?? throw new NotSupportedException("No controlled reader was configured.");
    }

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken)
    {
        ExecuteReaderAsyncCount++;
        LastReaderBehavior = behavior;
        LastReaderCancellationToken = cancellationToken;

        if (_executeReaderAsync is not null)
            return await _executeReaderAsync(behavior, cancellationToken).ConfigureAwait(false);

        return ExecuteDbDataReader(behavior);
    }
}

internal sealed class ControlledDbTransaction : DbTransaction
{
    private readonly DbConnection _connection;
    private readonly IsolationLevel _isolationLevel;
    private readonly Exception? _disposeException;
    private readonly Exception? _disposeAsyncException;
    private readonly Exception? _commitException;
    private readonly Exception? _commitAsyncException;
    private readonly Exception? _rollbackException;
    private readonly Exception? _rollbackAsyncException;
    private readonly TaskCompletionSource _commitAsyncGate =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _rollbackAsyncGate =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _disposeAsyncGate =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _commitSyncCount;
    private int _rollbackSyncCount;
    private int _disposeSyncCount;

    public ControlledDbTransaction(
        DbConnection connection,
        IsolationLevel isolationLevel,
        Exception? disposeException,
        Exception? disposeAsyncException,
        Exception? commitException,
        Exception? commitAsyncException,
        Exception? rollbackException,
        Exception? rollbackAsyncException,
        bool blockCommitAsync,
        bool blockRollbackAsync,
        bool blockDisposeAsync,
        TaskCompletionSource commitAsyncStarted,
        TaskCompletionSource rollbackAsyncStarted,
        TaskCompletionSource disposeAsyncStarted)
    {
        _connection = connection;
        _isolationLevel = isolationLevel;
        _disposeException = disposeException;
        _disposeAsyncException = disposeAsyncException;
        _commitException = commitException;
        _commitAsyncException = commitAsyncException;
        _rollbackException = rollbackException;
        _rollbackAsyncException = rollbackAsyncException;
        CommitAsyncStarted = commitAsyncStarted;
        RollbackAsyncStarted = rollbackAsyncStarted;
        DisposeAsyncStarted = disposeAsyncStarted;

        if (!blockCommitAsync)
            _commitAsyncGate.TrySetResult();

        if (!blockRollbackAsync)
            _rollbackAsyncGate.TrySetResult();

        if (!blockDisposeAsync)
            _disposeAsyncGate.TrySetResult();
    }

    public int CommitCount => CommitSyncCount + CommitAsyncCount;
    public int RollbackCount => RollbackSyncCount + RollbackAsyncCount;
    public int DisposeCount => DisposeSyncCount + TransactionDisposeAsyncCount;
    public int CommitSyncCount => Volatile.Read(ref _commitSyncCount);
    public int RollbackSyncCount => Volatile.Read(ref _rollbackSyncCount);
    public int DisposeSyncCount => Volatile.Read(ref _disposeSyncCount);
    public int CommitAsyncCount { get; private set; }
    public int RollbackAsyncCount { get; private set; }
    public int TransactionDisposeAsyncCount { get; private set; }
    public CancellationToken LastCommitCancellationToken { get; private set; }
    public CancellationToken LastRollbackCancellationToken { get; private set; }
    public TaskCompletionSource CommitAsyncStarted { get; }
    public TaskCompletionSource RollbackAsyncStarted { get; }
    public TaskCompletionSource DisposeAsyncStarted { get; }

    public override IsolationLevel IsolationLevel => _isolationLevel;
    protected override DbConnection DbConnection => _connection;

    public void ReleaseCommitAsync() => _commitAsyncGate.TrySetResult();
    public void ReleaseRollbackAsync() => _rollbackAsyncGate.TrySetResult();
    public void ReleaseDisposeAsync() => _disposeAsyncGate.TrySetResult();

    public override void Commit()
    {
        Interlocked.Increment(ref _commitSyncCount);
        if (_commitException is not null)
            throw _commitException;
    }

    public override async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        CommitAsyncCount++;
        LastCommitCancellationToken = cancellationToken;
        CommitAsyncStarted.TrySetResult();
        await _commitAsyncGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        if (_commitAsyncException is not null)
            throw _commitAsyncException;
    }

    public override void Rollback()
    {
        Interlocked.Increment(ref _rollbackSyncCount);
        if (_rollbackException is not null)
            throw _rollbackException;
    }

    public override async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        RollbackAsyncCount++;
        LastRollbackCancellationToken = cancellationToken;
        RollbackAsyncStarted.TrySetResult();
        await _rollbackAsyncGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        if (_rollbackAsyncException is not null)
            throw _rollbackAsyncException;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Interlocked.Increment(ref _disposeSyncCount);

        base.Dispose(disposing);

        if (disposing && _disposeException is not null)
            throw _disposeException;
    }

    public override async ValueTask DisposeAsync()
    {
        TransactionDisposeAsyncCount++;
        DisposeAsyncStarted.TrySetResult();
        await _disposeAsyncGate.Task.ConfigureAwait(false);

        if (_disposeAsyncException is not null)
            throw _disposeAsyncException;
    }
}
