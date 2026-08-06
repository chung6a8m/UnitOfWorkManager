using System.Data.Common;

namespace UnitOfWork.Tests.Fixtures;

public interface ICounterRepository
{
    void Insert(int value);
}

/// <summary>
/// Repository chỉ nhận DbConnection do UnitOfWork cung cấp; nó không tự quản lý
/// transaction của command.
/// </summary>
public class CounterRepository : ICounterRepository
{
    private readonly DbConnection _connection;

    public CounterRepository(DbConnection connection)
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
