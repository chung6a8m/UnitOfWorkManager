using System.Data.Common;
using RepoDb;
using UnitOfWork.Sample.WebApi.MinimalApi.Models;

namespace UnitOfWork.Sample.WebApi.MinimalApi.Repositories;

public sealed class RepoDbCounterRepository : ICounterRepository
{
    private readonly DbConnection _connection;

    public RepoDbCounterRepository(DbConnection connection)
    {
        _connection = connection;
    }

    public async Task InsertAsync(int value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _connection.InsertAsync("Counter", new { Value = value });
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task<IReadOnlyList<CounterItem>> ListAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var items = await _connection.ExecuteQueryAsync<CounterItem>(
            "SELECT Id, Value FROM Counter ORDER BY Id;");
        cancellationToken.ThrowIfCancellationRequested();
        return items.ToArray();
    }

    public async Task<long> CountAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var count = await _connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM Counter;");
        cancellationToken.ThrowIfCancellationRequested();
        return count;
    }

    public int GetConfiguredCommandTimeoutSeconds()
    {
        using var command = _connection.CreateCommand();
        return command.CommandTimeout;
    }
}
