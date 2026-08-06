using System.Data.Common;

namespace UnitOfWork.Core;

public interface IUnitOfWorkContext
{
    DbConnection Connection { get; }

    T GetRepository<T>() where T : class;
}
