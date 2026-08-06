using System.Data.Common;

namespace UnitOfWork.Core;

public interface IUnitOfWorkManager
{
    IUnitOfWorkContext Current { get; }
    bool HasCurrent { get; }

    Task<IUnitOfWorkScope> BeginAsync(
        UnitOfWorkOptions? options = null,
        CancellationToken cancellationToken = default);
}

public interface IDbConnectionFactory
{
    DbConnection CreateConnection();
}
