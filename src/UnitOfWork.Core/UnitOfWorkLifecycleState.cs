namespace UnitOfWork.Core;

internal enum UnitOfWorkLifecycleState
{
    Initializing,
    Active,
    Finalizing,
    Disposed,
    Faulted
}

internal enum UnitOfWorkCompletionOutcome
{
    None,
    Committed,
    RolledBack,
    Faulted
}

internal enum UnitOfWorkScopeOutcome
{
    Completed,
    RollbackRequested,
    Abandoned
}
