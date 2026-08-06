using System.Data;
using UnitOfWork.Core.Exceptions;

namespace UnitOfWork.Core;

internal enum UnitOfWorkScopeState
{
    Active,
    Completed,
    RollbackRequested,
    Abandoned
}

internal sealed class UnitOfWorkScope : IUnitOfWorkScope
{
    private readonly RootUnitOfWork _root;
    private readonly object _settlementLock = new();
    private UnitOfWorkScopeState _state = UnitOfWorkScopeState.Active;

    internal UnitOfWorkScope(RootUnitOfWork root)
    {
        _root = root;
    }

    public IDbConnection Connection => _root.Connection;

    public T GetRepository<T>() where T : class => _root.GetRepository<T>();

    public Task CompleteAsync() => SettleAsync(
        UnitOfWorkScopeState.Completed,
        UnitOfWorkScopeOutcome.Completed);

    public Task RollbackAsync() => SettleAsync(
        UnitOfWorkScopeState.RollbackRequested,
        UnitOfWorkScopeOutcome.RollbackRequested);

    public void Dispose()
    {
        if (IsSettled())
            return;

        try
        {
            _root.EnsureUsable();
        }
        catch (UnitOfWorkConcurrencyException) when (IsSettled())
        {
            return;
        }
        catch (UnitOfWorkStateException) when (IsSettled())
        {
            return;
        }

        Func<Task> settle;
        lock (_settlementLock)
        {
            if (_state != UnitOfWorkScopeState.Active)
                return;

            _state = UnitOfWorkScopeState.Abandoned;
            if (!_root.TrySettleScope(UnitOfWorkScopeOutcome.Abandoned, out settle))
            {
                _state = UnitOfWorkScopeState.Active;
                throw FinalizationDuringOperationException();
            }
        }

        settle().GetAwaiter().GetResult();
    }

    private async Task SettleAsync(UnitOfWorkScopeState settledState, UnitOfWorkScopeOutcome outcome)
    {
        ThrowIfSettled();
        _root.EnsureUsable();

        Func<Task> settle;
        lock (_settlementLock)
        {
            if (_state != UnitOfWorkScopeState.Active)
                throw AlreadySettledException();

            _state = settledState;
            if (!_root.TrySettleScope(outcome, out settle))
            {
                _state = UnitOfWorkScopeState.Active;
                throw FinalizationDuringOperationException();
            }
        }

        await settle();
    }

    private bool IsSettled()
    {
        lock (_settlementLock)
            return _state != UnitOfWorkScopeState.Active;
    }

    private void ThrowIfSettled()
    {
        if (IsSettled())
            throw AlreadySettledException();
    }

    private static UnitOfWorkStateException AlreadySettledException() =>
        new("A unit of work scope outcome has already been settled.");

    private static UnitOfWorkConcurrencyException FinalizationDuringOperationException() =>
        new("The root unit of work cannot be finalized while an operation is active.");
}
