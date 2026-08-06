using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace UnitOfWork.Tests.Fixtures;

internal sealed class AsyncOnlyDbConnection : DbConnection
{
    private const string SyncApiMessage = "Synchronous provider API was used.";
    private readonly TaskCompletionSource _releaseOperation = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ConnectionState _state;

    public int OpenAsyncCount { get; private set; }
    public int BeginTransactionAsyncCount { get; private set; }
    public int ExecuteNonQueryAsyncCount { get; private set; }
    public int ExecuteScalarAsyncCount { get; private set; }
    public int PrepareAsyncCount { get; private set; }
    public int CommitAsyncCount { get; private set; }
    public int RollbackAsyncCount { get; private set; }
    public int DisposeAsyncCount { get; private set; }
    public int CancelCount { get; private set; }
    public bool BlockOperations { get; set; }
    public CancellationToken LastCancellationToken { get; private set; }
    public TaskCompletionSource OperationStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void ReleaseOperation() => _releaseOperation.TrySetResult();

    [AllowNull]
    public override string ConnectionString { get; set; } = string.Empty;
    public override string Database => "AsyncOnly";
    public override string DataSource => "AsyncOnly";
    public override string ServerVersion => "1.0";
    public override ConnectionState State => _state;

    public override void ChangeDatabase(string databaseName) { }
    public override void Close() => _state = ConnectionState.Closed;
    public override void Open() => throw new InvalidOperationException(SyncApiMessage);

    public override Task OpenAsync(CancellationToken cancellationToken)
    {
        OpenAsyncCount++;
        LastCancellationToken = cancellationToken;
        _state = ConnectionState.Open;
        return Task.CompletedTask;
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        throw new InvalidOperationException(SyncApiMessage);

    protected override ValueTask<DbTransaction> BeginDbTransactionAsync(
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken)
    {
        BeginTransactionAsyncCount++;
        LastCancellationToken = cancellationToken;
        return ValueTask.FromResult<DbTransaction>(new AsyncOnlyDbTransaction(this, isolationLevel));
    }

    protected override DbCommand CreateDbCommand() => new AsyncOnlyDbCommand(this);

    public override async ValueTask DisposeAsync()
    {
        DisposeAsyncCount++;
        _state = ConnectionState.Closed;
        await base.DisposeAsync();
    }

    private sealed class AsyncOnlyDbTransaction : DbTransaction
    {
        private readonly AsyncOnlyDbConnection _connection;
        private readonly IsolationLevel _isolationLevel;

        public AsyncOnlyDbTransaction(AsyncOnlyDbConnection connection, IsolationLevel isolationLevel)
        {
            _connection = connection;
            _isolationLevel = isolationLevel;
        }

        public override IsolationLevel IsolationLevel => _isolationLevel;
        protected override DbConnection DbConnection => _connection;
        public override void Commit() => throw new InvalidOperationException(SyncApiMessage);
        public override void Rollback() => throw new InvalidOperationException(SyncApiMessage);

        public override Task CommitAsync(CancellationToken cancellationToken = default)
        {
            _connection.CommitAsyncCount++;
            _connection.LastCancellationToken = cancellationToken;
            return Task.CompletedTask;
        }

        public override Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            _connection.RollbackAsyncCount++;
            _connection.LastCancellationToken = cancellationToken;
            return Task.CompletedTask;
        }
    }

    private sealed class AsyncOnlyDbCommand : DbCommand
    {
        private readonly AsyncOnlyDbConnection _connection;
        private DbTransaction? _transaction;

        public AsyncOnlyDbCommand(AsyncOnlyDbConnection connection) => _connection = connection;

        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get => _connection; set { } }
        protected override DbTransaction? DbTransaction { get => _transaction; set => _transaction = value; }
        protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();

        public override void Cancel() => _connection.CancelCount++;
        public override int ExecuteNonQuery() => throw new InvalidOperationException(SyncApiMessage);
        public override object? ExecuteScalar() => throw new InvalidOperationException(SyncApiMessage);
        public override void Prepare() => throw new InvalidOperationException(SyncApiMessage);

        public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        {
            _connection.ExecuteNonQueryAsyncCount++;
            await WaitForOperationAsync(cancellationToken);
            return 7;
        }

        public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
        {
            _connection.ExecuteScalarAsyncCount++;
            await WaitForOperationAsync(cancellationToken);
            return "async scalar";
        }

        public override Task PrepareAsync(CancellationToken cancellationToken = default)
        {
            _connection.PrepareAsyncCount++;
            _connection.LastCancellationToken = cancellationToken;
            return Task.CompletedTask;
        }

        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
            throw new InvalidOperationException(SyncApiMessage);

        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
            CommandBehavior behavior,
            CancellationToken cancellationToken) =>
            Task.FromException<DbDataReader>(new InvalidOperationException(SyncApiMessage));

        private async Task WaitForOperationAsync(CancellationToken cancellationToken)
        {
            _connection.LastCancellationToken = cancellationToken;
            _connection.OperationStarted.TrySetResult();
            if (_connection.BlockOperations)
                await _connection._releaseOperation.Task.WaitAsync(cancellationToken);
        }
    }
}
