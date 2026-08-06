using System.Data;

namespace UnitOfWork.Core;

public interface IUnitOfWorkManager
{
    IUnitOfWorkContext Current { get; }
    bool HasCurrent { get; }

    Task<IUnitOfWorkScope> BeginAsync();
}

public interface IDbConnectionFactory
{
    IDbConnection CreateConnection();
}
