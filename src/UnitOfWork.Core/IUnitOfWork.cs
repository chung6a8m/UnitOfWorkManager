using System.Data;

namespace UnitOfWork.Core;

public interface IUnitOfWork : IDisposable
{
    IDbConnection Connection { get; }
    IDbTransaction? Transaction { get; }

    T GetRepository<T>() where T : class;

    Task CommitAsync();
    Task RollbackAsync();
}
