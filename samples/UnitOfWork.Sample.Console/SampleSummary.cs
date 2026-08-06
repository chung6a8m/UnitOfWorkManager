namespace UnitOfWork.Sample.ConsoleApp;

public sealed record SampleSummary(
    bool CommitPersisted,
    bool NestedIncompleteRolledBack,
    bool RepositoryCacheReused,
    int ObservedCommandTimeoutSeconds,
    bool CancellationObserved,
    bool ConcurrencyGuardObserved,
    IReadOnlyList<int> FinalValues)
{
    public bool AllPassed =>
        CommitPersisted &&
        NestedIncompleteRolledBack &&
        RepositoryCacheReused &&
        ObservedCommandTimeoutSeconds == 15 &&
        CancellationObserved &&
        ConcurrencyGuardObserved &&
        FinalValues.SequenceEqual(new[] { 10, 20 });
}
