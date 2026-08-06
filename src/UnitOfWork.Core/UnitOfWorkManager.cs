using System.Data;

namespace UnitOfWork.Core;

public class UnitOfWorkManager : IUnitOfWorkManager
{
    // static + AsyncLocal: "current UoW" theo dõi xuyên suốt 1 async call chain (1 flow logic).
    private static readonly AsyncLocal<UnitOfWork?> _current = new();

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly Func<Type, IDbConnection, IDbTransaction?, object> _repositoryFactory;

    public UnitOfWorkManager(
        IDbConnectionFactory connectionFactory,
        Func<Type, IDbConnection, IDbTransaction?, object> repositoryFactory)
    {
        _connectionFactory = connectionFactory;
        _repositoryFactory = repositoryFactory;
    }

    public bool HasCurrent => _current.Value != null;

    public IUnitOfWork Current => _current.Value
        ?? throw new InvalidOperationException("Chưa có UnitOfWork nào được bắt đầu.");

    public async Task<IUnitOfWork> BeginAsync()
    {
        if (_current.Value != null)
        {
            _current.Value.IncrementRef();
            return _current.Value;
        }

        var connection = _connectionFactory.CreateConnection();
        var uow = new UnitOfWork(connection, _repositoryFactory);
        await uow.BeginTransactionAsync();
        _current.Value = uow;
        return uow;
    }

    public void ClearCurrent() => _current.Value = null;

    /// <summary>
    /// Chỉ dùng cho test cleanup (mở qua InternalsVisibleTo). `_current` là AsyncLocal *tĩnh*
    /// dùng chung cho mọi instance UnitOfWorkManager trong process — nếu một test fail giữa
    /// chừng và bỏ lỡ ClearCurrent(), trạng thái cũ có thể rò rỉ sang test kế tiếp. Test base
    /// gọi hàm này trong Dispose() (luôn chạy dù test pass/fail) để đảm bảo sạch tuyệt đối.
    /// </summary>
    internal static void ResetAmbientStateForTests() => _current.Value = null;
}
