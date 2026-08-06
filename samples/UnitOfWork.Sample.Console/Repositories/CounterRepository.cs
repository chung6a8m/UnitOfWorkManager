using System.Data.Common;

namespace UnitOfWork.Sample.ConsoleApp.Repositories;

public sealed class CounterRepository : ICounterRepository
{
    private readonly DbConnection _connection;

    public CounterRepository(DbConnection connection)
    {
        _connection = connection;
    }

    public async Task InsertAsync(
        int value,
        CancellationToken cancellationToken = default)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO Counter (Value) VALUES ($value);";

        var parameter = command.CreateParameter();
        parameter.ParameterName = "$value";
        parameter.Value = value;
        command.Parameters.Add(parameter);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public int GetConfiguredCommandTimeoutSeconds()
    {
        using var command = _connection.CreateCommand();
        return command.CommandTimeout;
    }
}
