using UnitOfWork.Sample.WebApi.MinimalApi.Models;

namespace UnitOfWork.Sample.WebApi.MinimalApi.Repositories;

public interface ICounterRepository
{
    Task InsertAsync(int value, CancellationToken cancellationToken);

    Task<IReadOnlyList<CounterItem>> ListAsync(CancellationToken cancellationToken);

    Task<long> CountAsync(CancellationToken cancellationToken);

    int GetConfiguredCommandTimeoutSeconds();
}
