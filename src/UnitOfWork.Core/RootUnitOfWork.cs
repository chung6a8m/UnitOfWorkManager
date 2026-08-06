using System.Data;
using System.Data.Common;
using System.Runtime.ExceptionServices;
using UnitOfWork.Core.Exceptions;

namespace UnitOfWork.Core;

internal sealed class RootUnitOfWork : IUnitOfWorkContext
{
    private const string CleanupExceptionDataKey = "UnitOfWorkCleanupException";

    private readonly IDbConnection _connection;
    private readonly Func<Type, IDbConnection, object> _repositoryFactory;
    private readonly Dictionary<Type, object> _repositories = new();
    private readonly Func<bool> _isCurrentRoot;
    private readonly Action _onRootFinished;
    private readonly object _lifecycleLock = new();

    private IDbTransaction? _transaction;
    private IDbConnection? _boundConnection;
    private Task? _initializationTask;
    private int _activeScopeCount;
    private int _rollbackRequested;
    private int _lifecycleState = (int)UnitOfWorkLifecycleState.Initializing;
    private int _completionOutcome = (int)UnitOfWorkCompletionOutcome.None;
    private int _operationInProgress;

    internal RootUnitOfWork(
        IDbConnection connection,
        Func<Type, IDbConnection, object> repositoryFactory,
        Func<bool> isCurrentRoot,
        Action onRootFinished)
    {
        _connection = connection;
        _repositoryFactory = repositoryFactory;
        _isCurrentRoot = isCurrentRoot;
        _onRootFinished = onRootFinished;
    }

    internal int ActiveScopeCount => Volatile.Read(ref _activeScopeCount);
    internal bool RollbackRequested => Volatile.Read(ref _rollbackRequested) != 0;
    internal UnitOfWorkLifecycleState LifecycleState =>
        (UnitOfWorkLifecycleState)Volatile.Read(ref _lifecycleState);
    internal UnitOfWorkCompletionOutcome CompletionOutcome =>
        (UnitOfWorkCompletionOutcome)Volatile.Read(ref _completionOutcome);
    internal IDbConnection Connection
    {
        get
        {
            EnsureUsable();
            return _boundConnection
                ?? throw new UnitOfWorkStateException("The unit of work transaction has not been initialized.");
        }
    }
    internal IDbTransaction? Transaction => _transaction;

    IDbConnection IUnitOfWorkContext.Connection => Connection;

    T IUnitOfWorkContext.GetRepository<T>() => GetRepository<T>();

    internal UnitOfWorkScope AcquireScope()
    {
        lock (_lifecycleLock)
        {
            var state = LifecycleState;
            if (state is not UnitOfWorkLifecycleState.Initializing and not UnitOfWorkLifecycleState.Active)
                throw new UnitOfWorkStateException("The unit of work root is no longer accepting scopes.");

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
                if (_connection is DbConnection dbConnection)
                    await dbConnection.OpenAsync();
                else
                    _connection.Open();
            }

            _transaction = _connection.BeginTransaction();
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
                DisposeResources();
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

    internal bool TrySettleScope(UnitOfWorkScopeOutcome outcome, out Task settlement)
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

                if (Volatile.Read(ref _operationInProgress) != 0)
                {
                    settlement = Task.CompletedTask;
                    return false;
                }
            }

            if (outcome is UnitOfWorkScopeOutcome.RollbackRequested or UnitOfWorkScopeOutcome.Abandoned)
                Volatile.Write(ref _rollbackRequested, 1);

            var remainingScopes = Interlocked.Decrement(ref _activeScopeCount);
            if (remainingScopes != 0)
            {
                settlement = Task.CompletedTask;
                return true;
            }

            Volatile.Write(ref _lifecycleState, (int)UnitOfWorkLifecycleState.Finalizing);
        }

        settlement = FinalizeAsync();
        return true;
    }

    internal async Task<T> RunGuardedAsync<T>(Func<Task<T>> operation)
    {
        lock (_lifecycleLock)
        {
            EnsureUsable();

            if (Interlocked.CompareExchange(ref _operationInProgress, 1, 0) != 0)
            {
                throw new UnitOfWorkConcurrencyException(
                    "The root unit of work is already executing another operation.");
            }
        }

        try
        {
            return await operation();
        }
        finally
        {
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
            throw new UnitOfWorkConcurrencyException("The current execution flow does not own this unit of work root.");

        if (LifecycleState != UnitOfWorkLifecycleState.Active)
            throw new UnitOfWorkStateException("The unit of work root is not active.");
    }

    internal IDbCommand CreateTransactionBoundCommand()
    {
        EnsureUsable();

        var transaction = _transaction
            ?? throw new UnitOfWorkStateException("The unit of work transaction has not been initialized.");
        var command = _connection.CreateCommand();
        command.Transaction = transaction;
        return command;
    }

    internal string GetConnectionString() => _connection.ConnectionString;
    internal int GetConnectionTimeout() => _connection.ConnectionTimeout;
    internal string GetDatabase() => _connection.Database;
    internal ConnectionState GetConnectionState() => _connection.State;

    internal IsolationLevel GetTransactionIsolationLevel() => (_transaction
        ?? throw new UnitOfWorkStateException("The unit of work transaction has not been initialized."))
        .IsolationLevel;

    private Task FinalizeAsync()
    {
        try
        {
            var outcome = RollbackRequested
                ? UnitOfWorkCompletionOutcome.RolledBack
                : UnitOfWorkCompletionOutcome.Committed;

            if (outcome == UnitOfWorkCompletionOutcome.RolledBack)
                _transaction?.Rollback();
            else
                _transaction?.Commit();

            Volatile.Write(ref _completionOutcome, (int)outcome);
            Volatile.Write(ref _lifecycleState, (int)UnitOfWorkLifecycleState.Disposed);
        }
        catch
        {
            Volatile.Write(ref _completionOutcome, (int)UnitOfWorkCompletionOutcome.Faulted);
            Volatile.Write(ref _lifecycleState, (int)UnitOfWorkLifecycleState.Faulted);
            throw;
        }
        finally
        {
            try
            {
                DisposeResources();
            }
            finally
            {
                _onRootFinished();
            }
        }

        return Task.CompletedTask;
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

    private void DisposeResources()
    {
        Exception? transactionError = null;
        Exception? connectionError = null;

        try
        {
            _transaction?.Dispose();
        }
        catch (Exception exception)
        {
            transactionError = exception;
        }

        try
        {
            _connection.Dispose();
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
