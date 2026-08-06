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
    private int _state = (int)UnitOfWorkScopeState.Active;

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
        if ((UnitOfWorkScopeState)Volatile.Read(ref _state) != UnitOfWorkScopeState.Active)
            return;

        try
        {
            _root.EnsureUsable();
        }
        catch (UnitOfWorkConcurrencyException) when (
            (UnitOfWorkScopeState)Volatile.Read(ref _state) != UnitOfWorkScopeState.Active)
        {
            return;
        }
        catch (UnitOfWorkStateException) when (
            (UnitOfWorkScopeState)Volatile.Read(ref _state) != UnitOfWorkScopeState.Active)
        {
            return;
        }

        if (Interlocked.CompareExchange(
                ref _state,
                (int)UnitOfWorkScopeState.Abandoned,
                (int)UnitOfWorkScopeState.Active) == (int)UnitOfWorkScopeState.Active)
        {
            if (!_root.TrySettleScope(UnitOfWorkScopeOutcome.Abandoned, out var settlement))
            {
                Interlocked.CompareExchange(
                    ref _state,
                    (int)UnitOfWorkScopeState.Active,
                    (int)UnitOfWorkScopeState.Abandoned);
                throw FinalizationDuringOperationException();
            }

            settlement.GetAwaiter().GetResult();
        }
    }

    private async Task SettleAsync(UnitOfWorkScopeState settledState, UnitOfWorkScopeOutcome outcome)
    {
        _root.EnsureUsable();

        if (Interlocked.CompareExchange(
                ref _state,
                (int)settledState,
                (int)UnitOfWorkScopeState.Active) != (int)UnitOfWorkScopeState.Active)
        {
            throw new UnitOfWorkStateException("A unit of work scope outcome has already been settled.");
        }

        if (!_root.TrySettleScope(outcome, out var settlement))
        {
            Interlocked.CompareExchange(
                ref _state,
                (int)UnitOfWorkScopeState.Active,
                (int)settledState);
            throw FinalizationDuringOperationException();
        }

        await settlement;
    }

    private static UnitOfWorkConcurrencyException FinalizationDuringOperationException() =>
        new("The root unit of work cannot be finalized while an operation is active.");
}
