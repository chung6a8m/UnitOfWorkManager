using System.Data;

namespace UnitOfWork.Core;

public interface IUnitOfWorkManager
{
    IUnitOfWork Current { get; }
    bool HasCurrent { get; }

    Task<IUnitOfWork> BeginAsync();

    /// <summary>Reset AsyncLocal hiện tại — gọi khi lời gọi ngoài cùng (outermost) kết thúc.</summary>
    void ClearCurrent();
}

public interface IDbConnectionFactory
{
    IDbConnection CreateConnection();
}
