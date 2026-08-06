using UnitOfWork.Sample.WebApi.Models;

namespace UnitOfWork.Sample.WebApi.Repositories;

public interface ICounterRepository
{
    Task InsertAsync(int value, CancellationToken cancellationToken);

    Task<IReadOnlyList<CounterItem>> ListAsync(CancellationToken cancellationToken);

    Task<long> CountAsync(CancellationToken cancellationToken);

    int GetConfiguredCommandTimeoutSeconds();
}
