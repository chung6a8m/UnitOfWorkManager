namespace UnitOfWork.Core;

public interface IUnitOfWorkScope : IUnitOfWorkContext, IDisposable, IAsyncDisposable
{
    Task CompleteAsync(CancellationToken cancellationToken = default);

    Task RollbackAsync(CancellationToken cancellationToken = default);
}
