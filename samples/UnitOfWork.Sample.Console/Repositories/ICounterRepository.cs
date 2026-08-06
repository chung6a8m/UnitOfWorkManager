namespace UnitOfWork.Sample.ConsoleApp.Repositories;

public interface ICounterRepository
{
    Task InsertAsync(int value, CancellationToken cancellationToken = default);

    int GetConfiguredCommandTimeoutSeconds();
}
