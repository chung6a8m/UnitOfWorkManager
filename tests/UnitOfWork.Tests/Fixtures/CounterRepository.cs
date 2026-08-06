using System.Data;

namespace UnitOfWork.Tests.Fixtures;

public interface ICounterRepository
{
    void Insert(int value);
}

/// <summary>
/// Repository "thật" chạy qua IDbConnection được UnitOfWork bọc (GuardedDbConnection) —
/// dùng để chứng minh guard hoạt động xuyên suốt cả tầng Repository, không chỉ ở
/// CommitAsync/RollbackAsync.
/// </summary>
public class CounterRepository : ICounterRepository
{
    private readonly IDbConnection _connection;
    private readonly IDbTransaction? _transaction;

    public CounterRepository(IDbConnection connection, IDbTransaction? transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    public void Insert(int value)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = "INSERT INTO Counter (Value) VALUES ($value);";
        var p = cmd.CreateParameter();
        p.ParameterName = "$value";
        p.Value = value;
        cmd.Parameters.Add(p);
        cmd.ExecuteNonQuery();
    }
}
