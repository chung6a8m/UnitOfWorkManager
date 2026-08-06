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
        if (Interlocked.CompareExchange(
                ref _state,
                (int)UnitOfWorkScopeState.Abandoned,
                (int)UnitOfWorkScopeState.Active) == (int)UnitOfWorkScopeState.Active)
        {
            _root.SettleScopeAsync(UnitOfWorkScopeOutcome.Abandoned).GetAwaiter().GetResult();
        }
    }

    private Task SettleAsync(UnitOfWorkScopeState settledState, UnitOfWorkScopeOutcome outcome)
    {
        if (Interlocked.CompareExchange(
                ref _state,
                (int)settledState,
                (int)UnitOfWorkScopeState.Active) != (int)UnitOfWorkScopeState.Active)
        {
            throw new UnitOfWorkStateException("A unit of work scope outcome has already been settled.");
        }

        return _root.SettleScopeAsync(outcome);
    }
}
