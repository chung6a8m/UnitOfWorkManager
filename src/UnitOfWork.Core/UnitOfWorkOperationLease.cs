namespace UnitOfWork.Core;

internal sealed class UnitOfWorkOperationLease : IDisposable
{
    private RootUnitOfWork? _owner;

    internal UnitOfWorkOperationLease(RootUnitOfWork owner) => _owner = owner;

    public void Dispose() =>
        Interlocked.Exchange(ref _owner, null)?.ReleaseOperation();
}
