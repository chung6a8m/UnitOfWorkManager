using System.Data.Common;
using UnitOfWork.Core.Exceptions;

namespace UnitOfWork.Core;

internal enum UnitOfWorkScopeState
{
    Reserved,
    Active,
    Completed,
    RollbackRequested,
    Abandoned,
    CanceledBeforeActivation
}

internal sealed class UnitOfWorkScope : IUnitOfWorkScope
{
    private readonly RootUnitOfWork _root;
    private readonly object _settlementLock = new();
    private UnitOfWorkScopeState _state = UnitOfWorkScopeState.Reserved;

    internal UnitOfWorkScope(RootUnitOfWork root)
    {
        _root = root;
    }

    public DbConnection Connection => _root.Connection;

    public T GetRepository<T>() where T : class => _root.GetRepository<T>();

    internal void Activate()
    {
        lock (_settlementLock)
        {
            if (_state != UnitOfWorkScopeState.Reserved)
            {
                throw new UnitOfWorkStateException(
                    "The unit of work scope reservation is no longer active.");
            }

            _state = UnitOfWorkScopeState.Active;
        }
    }

    internal Action? TryCancelBeforeActivation(Func<Action?> releaseReservation)
    {
        lock (_settlementLock)
        {
            if (_state != UnitOfWorkScopeState.Reserved)
                return null;

            var afterReservationRelease = releaseReservation();
            _state = UnitOfWorkScopeState.CanceledBeforeActivation;
            return afterReservationRelease;
        }
    }

    public Task CompleteAsync(CancellationToken cancellationToken = default) => SettleAsync(
        UnitOfWorkScopeState.Completed,
        UnitOfWorkScopeOutcome.Completed,
        cancellationToken);

    public Task RollbackAsync(CancellationToken cancellationToken = default) => SettleAsync(
        UnitOfWorkScopeState.RollbackRequested,
        UnitOfWorkScopeOutcome.RollbackRequested,
        cancellationToken);

    public void Dispose() =>
        DisposeAsync().AsTask().ConfigureAwait(false).GetAwaiter().GetResult();

    public ValueTask DisposeAsync() => new(DisposeAsyncCore());

    private async Task DisposeAsyncCore()
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

        Func<CancellationToken, Task> settle;
        lock (_settlementLock)
        {
            if (_state is not UnitOfWorkScopeState.Reserved and not UnitOfWorkScopeState.Active)
                return;

            _state = UnitOfWorkScopeState.Abandoned;
            if (!_root.TrySettleScope(UnitOfWorkScopeOutcome.Abandoned, out settle))
            {
                _state = UnitOfWorkScopeState.Active;
                throw FinalizationDuringOperationException();
            }
        }

        await settle(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task SettleAsync(
        UnitOfWorkScopeState settledState,
        UnitOfWorkScopeOutcome outcome,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfSettled();
        _root.EnsureUsable();

        Func<CancellationToken, Task> settle;
        lock (_settlementLock)
        {
            if (_state is not UnitOfWorkScopeState.Reserved and not UnitOfWorkScopeState.Active)
                throw AlreadySettledException();

            _state = settledState;
            if (!_root.TrySettleScope(outcome, out settle))
            {
                _state = UnitOfWorkScopeState.Active;
                throw FinalizationDuringOperationException();
            }
        }

        await settle(cancellationToken).ConfigureAwait(false);
    }

    private bool IsSettled()
    {
        lock (_settlementLock)
        {
            return _state is not UnitOfWorkScopeState.Reserved
                and not UnitOfWorkScopeState.Active;
        }
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
