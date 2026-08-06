using System.Data;

namespace UnitOfWork.Tests.Fixtures;

public interface ICounterRepository
{
    void Insert(int value);
}

/// <summary>
/// Repository chỉ nhận IDbConnection do UnitOfWork cung cấp; nó không tự quản lý
/// transaction của command.
/// </summary>
public class CounterRepository : ICounterRepository
{
    private readonly IDbConnection _connection;

    public CounterRepository(IDbConnection connection)
    {
        _connection = connection;
    }

    public void Insert(int value)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT INTO Counter (Value) VALUES ($value);";
        var p = cmd.CreateParameter();
        p.ParameterName = "$value";
        p.Value = value;
        cmd.Parameters.Add(p);
        cmd.ExecuteNonQuery();
    }
}
