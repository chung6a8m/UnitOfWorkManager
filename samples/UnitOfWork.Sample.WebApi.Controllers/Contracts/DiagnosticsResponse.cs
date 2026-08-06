namespace UnitOfWork.Sample.WebApi.Contracts;

public sealed record DiagnosticsResponse(
    bool RepositoryCacheReused,
    int CommandTimeoutSeconds,
    bool CancellationObserved,
    bool AmbientClearedAfterCancellation,
    bool ConcurrencyGuardObserved)
{
    public bool AllPassed =>
        RepositoryCacheReused &&
        CommandTimeoutSeconds == 15 &&
        CancellationObserved &&
        AmbientClearedAfterCancellation &&
        ConcurrencyGuardObserved;
}
