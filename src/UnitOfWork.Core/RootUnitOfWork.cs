using System.Data;
using System.Data.Common;
using System.Runtime.ExceptionServices;
using UnitOfWork.Core.Exceptions;

namespace UnitOfWork.Core;

internal sealed class RootUnitOfWork
{
    private readonly IDbConnection _connection;
    private readonly Func<Type, IDbConnection, IDbTransaction?, object> _repositoryFactory;
    private readonly Dictionary<Type, object> _repositories = new();
    private readonly Func<bool> _isCurrentRoot;
    private readonly Action _onRootFinished;

    private IDbTransaction? _transaction;
    private int _activeScopeCount;
    private int _rollbackRequested;
    private int _lifecycleState = (int)UnitOfWorkLifecycleState.Initializing;
    private int _completionOutcome = (int)UnitOfWorkCompletionOutcome.None;
    private int _operationInProgress;

    internal RootUnitOfWork(
        IDbConnection connection,
        Func<Type, IDbConnection, IDbTransaction?, object> repositoryFactory,
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
    internal IDbConnection Connection => _connection;
    internal IDbTransaction? Transaction => _transaction;

    internal UnitOfWorkScope AcquireScope()
    {
        var state = LifecycleState;
        if (state is not UnitOfWorkLifecycleState.Initializing and not UnitOfWorkLifecycleState.Active)
            throw new UnitOfWorkStateException("The unit of work root is no longer accepting scopes.");

        Interlocked.Increment(ref _activeScopeCount);
        return new UnitOfWorkScope(this);
    }

    internal async Task InitializeAsync()
    {
        if (Interlocked.CompareExchange(
                ref _lifecycleState,
                (int)UnitOfWorkLifecycleState.Initializing,
                (int)UnitOfWorkLifecycleState.Initializing) != (int)UnitOfWorkLifecycleState.Initializing)
        {
            throw new UnitOfWorkStateException("The unit of work root has already been initialized.");
        }

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
            Volatile.Write(ref _lifecycleState, (int)UnitOfWorkLifecycleState.Active);
        }
        catch
        {
            Volatile.Write(ref _completionOutcome, (int)UnitOfWorkCompletionOutcome.Faulted);
            Volatile.Write(ref _lifecycleState, (int)UnitOfWorkLifecycleState.Faulted);
            DisposeResources();
            _onRootFinished();
            throw;
        }
    }

    internal Task SettleScopeAsync(UnitOfWorkScopeOutcome outcome)
    {
        if (outcome is UnitOfWorkScopeOutcome.RollbackRequested or UnitOfWorkScopeOutcome.Abandoned)
            Volatile.Write(ref _rollbackRequested, 1);

        var remainingScopes = Interlocked.Decrement(ref _activeScopeCount);
        if (remainingScopes < 0)
            throw new UnitOfWorkStateException("A unit of work scope was settled more than once.");

        if (remainingScopes != 0)
            return Task.CompletedTask;

        return FinalizeAsync();
    }

    internal async Task<T> RunGuardedAsync<T>(Func<Task<T>> operation)
    {
        EnsureUsable();

        if (Interlocked.CompareExchange(ref _operationInProgress, 1, 0) != 0)
        {
            throw new UnitOfWorkConcurrencyException(
                "The root unit of work is already executing another operation.");
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
            repository = _repositoryFactory(type, _connection, _transaction);
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

    private Task FinalizeAsync()
    {
        if (Interlocked.CompareExchange(
                ref _lifecycleState,
                (int)UnitOfWorkLifecycleState.Finalizing,
                (int)UnitOfWorkLifecycleState.Active) != (int)UnitOfWorkLifecycleState.Active)
        {
            throw new UnitOfWorkStateException("The unit of work root cannot be finalized in its current state.");
        }

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
            DisposeResources();
            _onRootFinished();
        }

        return Task.CompletedTask;
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
