using System.Data;
using System.Data.Common;
using System.Runtime.ExceptionServices;
using UnitOfWork.Core.Exceptions;

namespace UnitOfWork.Core;

internal sealed class RootUnitOfWork : IUnitOfWorkContext
{
    private const string CleanupExceptionDataKey = "UnitOfWorkCleanupException";

    private static readonly UnitOfWorkOptions DefaultOptions = new();

    private readonly DbConnection _connection;
    private readonly Func<Type, DbConnection, object> _repositoryFactory;
    private readonly IUnitOfWorkTransactionFactory _transactionFactory;
    private readonly Dictionary<Type, object> _repositories = new();
    private readonly Func<bool> _isCurrentRoot;
    private readonly Action _onRootFinished;
    private readonly object _lifecycleLock = new();
    private readonly CancellationTokenSource _initializationCancellation = new();

    private DbTransaction? _transaction;
    private DbConnection? _boundConnection;
    private Task? _initializationTask;
    private int _activeScopeCount;
    private int _rollbackRequested;
    private int _lifecycleState = (int)UnitOfWorkLifecycleState.Initializing;
    private int _completionOutcome = (int)UnitOfWorkCompletionOutcome.None;
    private int _operationInProgress;
    private int _initializationCancellationRequested;
    private string? _activeOperationName;

    internal RootUnitOfWork(
        DbConnection connection,
        Func<Type, DbConnection, object> repositoryFactory,
        Func<bool> isCurrentRoot,
        Action onRootFinished,
        UnitOfWorkOptions? options = null,
        IUnitOfWorkTransactionFactory? transactionFactory = null)
    {
        _connection = connection;
        _repositoryFactory = repositoryFactory;
        _isCurrentRoot = isCurrentRoot;
        _onRootFinished = onRootFinished;
        Options = (options ?? DefaultOptions).Validate();
        _transactionFactory = transactionFactory ?? new DefaultUnitOfWorkTransactionFactory();
    }

    internal int ActiveScopeCount => Volatile.Read(ref _activeScopeCount);
    internal bool RollbackRequested => Volatile.Read(ref _rollbackRequested) != 0;
    internal UnitOfWorkLifecycleState LifecycleState =>
        (UnitOfWorkLifecycleState)Volatile.Read(ref _lifecycleState);
    internal UnitOfWorkCompletionOutcome CompletionOutcome =>
        (UnitOfWorkCompletionOutcome)Volatile.Read(ref _completionOutcome);
    internal UnitOfWorkOptions Options { get; }
    internal DbConnection Connection
    {
        get
        {
            EnsureUsable();
            return _boundConnection
                ?? throw new UnitOfWorkStateException("The unit of work transaction has not been initialized.");
        }
    }
    internal DbTransaction? Transaction => _transaction;
    internal bool HasActiveOperation => Volatile.Read(ref _operationInProgress) != 0;
    internal bool InitializationCancellationRequested =>
        Volatile.Read(ref _initializationCancellationRequested) != 0;
    internal string? ActiveOperationName => Volatile.Read(ref _activeOperationName);

    DbConnection IUnitOfWorkContext.Connection => Connection;

    T IUnitOfWorkContext.GetRepository<T>() => GetRepository<T>();

    internal UnitOfWorkScope AcquireScope()
    {
        lock (_lifecycleLock)
        {
            var state = LifecycleState;
            if (state is not UnitOfWorkLifecycleState.Initializing and not UnitOfWorkLifecycleState.Active)
                throw new UnitOfWorkStateException("The unit of work root is no longer accepting scopes.");

            if (InitializationCancellationRequested)
                throw new UnitOfWorkStateException("The unit of work root initialization was canceled.");

            Interlocked.Increment(ref _activeScopeCount);
            return new UnitOfWorkScope(this);
        }
    }

    internal Task InitializeAsync()
    {
        TaskCompletionSource? completion = null;

        lock (_lifecycleLock)
        {
            if (_initializationTask is not null)
                return _initializationTask;

            if (LifecycleState != UnitOfWorkLifecycleState.Initializing)
                throw new UnitOfWorkStateException("The unit of work root has already been initialized.");

            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _initializationTask = completion.Task;
        }

        _ = InitializeCoreAsync(completion);
        return completion.Task;
    }

    private async Task InitializeCoreAsync(TaskCompletionSource completion)
    {
        try
        {
            if (_connection.State != ConnectionState.Open)
            {
                await _connection.OpenAsync(_initializationCancellation.Token)
                    .ConfigureAwait(false);
            }

            _transaction = await _transactionFactory.BeginTransactionAsync(
                _connection,
                Options,
                _initializationCancellation.Token).ConfigureAwait(false);
            _boundConnection = new TransactionBoundDbConnection(this);
            Volatile.Write(ref _lifecycleState, (int)UnitOfWorkLifecycleState.Active);
            completion.TrySetResult();
        }
        catch (Exception initializationError)
        {
            Volatile.Write(ref _completionOutcome, (int)UnitOfWorkCompletionOutcome.Faulted);
            Volatile.Write(ref _lifecycleState, (int)UnitOfWorkLifecycleState.Faulted);

            try
            {
                await DisposeResourcesAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupError)
            {
                AddCleanupException(initializationError, cleanupError);
            }
            finally
            {
                try
                {
                    _onRootFinished();
                }
                catch (Exception cleanupError)
                {
                    AddCleanupException(initializationError, cleanupError);
                }
            }

            completion.TrySetException(initializationError);
        }
    }

    internal void CancelScopeBeforeActivation(UnitOfWorkScope scope)
    {
        var cancelInitialization = false;

        lock (_lifecycleLock)
        {
            if (!scope.TryCancelBeforeActivation())
                return;

            var activeScopeCount = Volatile.Read(ref _activeScopeCount);
            if (activeScopeCount <= 0)
            {
                throw new UnitOfWorkStateException(
                    "A unit of work scope reservation was released more than once.");
            }

            var remainingScopes = Interlocked.Decrement(ref _activeScopeCount);
            if (remainingScopes == 0 && LifecycleState == UnitOfWorkLifecycleState.Initializing)
            {
                Volatile.Write(ref _initializationCancellationRequested, 1);
                cancelInitialization = true;
            }
        }

        if (cancelInitialization)
            _initializationCancellation.Cancel();
    }

    internal bool TrySettleScope(
        UnitOfWorkScopeOutcome outcome,
        out Func<CancellationToken, Task> settle)
    {
        lock (_lifecycleLock)
        {
            var activeScopeCount = Volatile.Read(ref _activeScopeCount);
            if (activeScopeCount <= 0)
                throw new UnitOfWorkStateException("A unit of work scope was settled more than once.");

            var finalizesRoot = activeScopeCount == 1;
            if (finalizesRoot)
            {
                if (LifecycleState != UnitOfWorkLifecycleState.Active)
                    throw new UnitOfWorkStateException("The unit of work root cannot be finalized in its current state.");

                if (HasActiveOperation)
                {
                    settle = static _ => Task.CompletedTask;
                    return false;
                }
            }

            if (outcome is UnitOfWorkScopeOutcome.RollbackRequested or UnitOfWorkScopeOutcome.Abandoned)
                Volatile.Write(ref _rollbackRequested, 1);

            var remainingScopes = Interlocked.Decrement(ref _activeScopeCount);
            if (remainingScopes != 0)
            {
                settle = static _ => Task.CompletedTask;
                return true;
            }

            Volatile.Write(ref _lifecycleState, (int)UnitOfWorkLifecycleState.Finalizing);

            var requestedOutcome = RollbackRequested
                ? UnitOfWorkCompletionOutcome.RolledBack
                : UnitOfWorkCompletionOutcome.Committed;
            settle = cancellationToken => FinalizeAsync(requestedOutcome, cancellationToken);
            return true;
        }
    }

    internal UnitOfWorkOperationLease EnterOperation(string operationName)
    {
        lock (_lifecycleLock)
        {
            EnsureUsable();

            if (Interlocked.CompareExchange(ref _operationInProgress, 1, 0) != 0)
            {
                throw new UnitOfWorkConcurrencyException(
                    $"The root unit of work is already executing another operation " +
                    $"('{ActiveOperationName}'); operation '{operationName}' was rejected.");
            }

            Volatile.Write(ref _activeOperationName, operationName);
            return new UnitOfWorkOperationLease(this);
        }
    }

    internal void ReleaseOperation()
    {
        lock (_lifecycleLock)
        {
            Volatile.Write(ref _activeOperationName, null);
            Interlocked.Exchange(ref _operationInProgress, 0);
        }
    }

    internal T GetRepository<T>() where T : class
    {
        EnsureUsable();

        var type = typeof(T);
        if (!_repositories.TryGetValue(type, out var repository))
        {
            repository = _repositoryFactory(type, Connection);
            _repositories[type] = repository;
        }

        return (T)repository;
    }

    internal void EnsureUsable()
    {
        if (!_isCurrentRoot())
        {
            throw new UnitOfWorkConcurrencyException(
                "The current root for this manager is missing or foreign in this execution flow.");
        }

        if (LifecycleState != UnitOfWorkLifecycleState.Active)
            throw new UnitOfWorkStateException("The unit of work root is not active.");
    }

    internal DbCommand CreateTransactionBoundCommand()
    {
        EnsureUsable();

        var transaction = _transaction
            ?? throw new UnitOfWorkStateException("The unit of work transaction has not been initialized.");
        var command = _connection.CreateCommand();
        command.Transaction = transaction;
        if (Options.CommandTimeoutSeconds is { } timeout)
            command.CommandTimeout = timeout;
        return command;
    }

    internal string GetConnectionString() => _connection.ConnectionString;
    internal int GetConnectionTimeout() => _connection.ConnectionTimeout;
    internal string GetDatabase() => _connection.Database;
    internal string GetDataSource() => _connection.DataSource;
    internal string GetServerVersion() => _connection.ServerVersion;
    internal ConnectionState GetConnectionState() => _connection.State;

    internal IsolationLevel GetTransactionIsolationLevel() => (_transaction
        ?? throw new UnitOfWorkStateException("The unit of work transaction has not been initialized."))
        .IsolationLevel;

    private async Task FinalizeAsync(
        UnitOfWorkCompletionOutcome requestedOutcome,
        CancellationToken cancellationToken)
    {
        Exception? primaryError = null;

        try
        {
            if (requestedOutcome == UnitOfWorkCompletionOutcome.RolledBack)
            {
                await _transaction!.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _transaction!.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            Volatile.Write(ref _completionOutcome, (int)requestedOutcome);
        }
        catch (Exception error)
        {
            primaryError = error;
            Volatile.Write(ref _completionOutcome, (int)UnitOfWorkCompletionOutcome.Faulted);
            Volatile.Write(ref _lifecycleState, (int)UnitOfWorkLifecycleState.Faulted);
            throw;
        }
        finally
        {
            try
            {
                await DisposeResourcesAsync().ConfigureAwait(false);
                if (primaryError is null)
                {
                    Volatile.Write(
                        ref _lifecycleState,
                        (int)UnitOfWorkLifecycleState.Disposed);
                }
            }
            catch (Exception cleanupError) when (primaryError is not null)
            {
                AddCleanupException(primaryError, cleanupError);
            }
            catch
            {
                Volatile.Write(ref _lifecycleState, (int)UnitOfWorkLifecycleState.Faulted);
                throw;
            }
            finally
            {
                _onRootFinished();
            }
        }
    }

    private static void AddCleanupException(Exception primaryException, Exception cleanupException)
    {
        if (primaryException.Data[CleanupExceptionDataKey] is Exception existingCleanupException)
        {
            primaryException.Data[CleanupExceptionDataKey] = new AggregateException(
                existingCleanupException,
                cleanupException);
            return;
        }

        primaryException.Data[CleanupExceptionDataKey] = cleanupException;
    }

    private async Task DisposeResourcesAsync()
    {
        Exception? transactionError = null;
        Exception? connectionError = null;

        try
        {
            if (_transaction is not null)
                await _transaction.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            transactionError = exception;
        }

        try
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            connectionError = exception;
        }

        if (transactionError is not null && connectionError is not null)
            throw new AggregateException(transactionError, connectionError);

        if (transactionError is not null)
            ExceptionDispatchInfo.Capture(transactionError).Throw();

        if (connectionError is not null)
            ExceptionDispatchInfo.Capture(connectionError).Throw();
    }
}
