namespace UnitOfWork.Core;

public interface IUnitOfWorkScope : IUnitOfWorkContext, IDisposable
{
    Task CompleteAsync();

    Task RollbackAsync();
}
