namespace UnitOfWork.Core;

public interface IUnitOfWorkScope : IUnitOfWorkContext, IDisposable, IAsyncDisposable
{
    Task CompleteAsync();

    Task RollbackAsync();
}
