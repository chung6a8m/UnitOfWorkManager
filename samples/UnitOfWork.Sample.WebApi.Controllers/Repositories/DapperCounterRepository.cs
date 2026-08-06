using System.Data.Common;
using Dapper;
using UnitOfWork.Sample.WebApi.Models;

namespace UnitOfWork.Sample.WebApi.Repositories;

public sealed class DapperCounterRepository : ICounterRepository
{
    private readonly DbConnection _connection;

    public DapperCounterRepository(DbConnection connection)
    {
        _connection = connection;
    }

    public async Task InsertAsync(int value, CancellationToken cancellationToken)
    {
        var command = new CommandDefinition(
            "INSERT INTO Counter (Value) VALUES (@Value);",
            new { Value = value },
            cancellationToken: cancellationToken);

        await _connection.ExecuteAsync(command);
    }

    public async Task<IReadOnlyList<CounterItem>> ListAsync(
        CancellationToken cancellationToken)
    {
        var command = new CommandDefinition(
            "SELECT Id, Value FROM Counter ORDER BY Id;",
            cancellationToken: cancellationToken);
        var items = await _connection.QueryAsync<CounterItem>(command);
        return items.ToArray();
    }

    public Task<long> CountAsync(CancellationToken cancellationToken)
    {
        var command = new CommandDefinition(
            "SELECT COUNT(*) FROM Counter;",
            cancellationToken: cancellationToken);
        return _connection.ExecuteScalarAsync<long>(command);
    }

    public int GetConfiguredCommandTimeoutSeconds()
    {
        using var command = _connection.CreateCommand();
        return command.CommandTimeout;
    }
}
