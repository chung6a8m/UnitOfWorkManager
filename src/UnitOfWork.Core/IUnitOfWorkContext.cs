using System.Data;

namespace UnitOfWork.Core;

public interface IUnitOfWorkContext
{
    IDbConnection Connection { get; }

    T GetRepository<T>() where T : class;
}
