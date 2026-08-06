using System.Data;

namespace UnitOfWork.Core;

public class UnitOfWorkManager : IUnitOfWorkManager
{
    private const string CleanupExceptionDataKey = "UnitOfWorkCleanupException";

    private sealed class AmbientUnitOfWorkHolder
    {
        public UnitOfWork? UnitOfWork;
        public Task<IUnitOfWork>? Initialization;
    }

    // static + AsyncLocal: "current UoW" theo dõi xuyên suốt 1 async call chain (1 flow logic).
    // Holder mutable cho phép async helper dọn state mà caller vẫn quan sát được.
    private static readonly AsyncLocal<AmbientUnitOfWorkHolder?> _current = new();

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly Func<Type, IDbConnection, IDbTransaction?, object> _repositoryFactory;

    public UnitOfWorkManager(
        IDbConnectionFactory connectionFactory,
        Func<Type, IDbConnection, IDbTransaction?, object> repositoryFactory)
    {
        _connectionFactory = connectionFactory;
        _repositoryFactory = repositoryFactory;
    }

    public bool HasCurrent => _current.Value?.UnitOfWork != null;

    public IUnitOfWork Current => _current.Value?.UnitOfWork
        ?? throw new InvalidOperationException("Chưa có UnitOfWork nào được bắt đầu.");

    public Task<IUnitOfWork> BeginAsync()
    {
        var currentHolder = _current.Value;
        if (currentHolder?.UnitOfWork is { } current)
        {
            current.IncrementRef();
            return currentHolder.Initialization
                ?? Task.FromResult<IUnitOfWork>(current);
        }

        var connection = _connectionFactory.CreateConnection();
        var uow = new UnitOfWork(connection, _repositoryFactory);
        var completion = new TaskCompletionSource<IUnitOfWork>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = new AmbientUnitOfWorkHolder
        {
            UnitOfWork = uow,
            Initialization = completion.Task
        };
        _current.Value = holder;

        _ = InitializeAsync(uow, holder, completion);
        return completion.Task;
    }

    private static async Task InitializeAsync(
        UnitOfWork uow,
        AmbientUnitOfWorkHolder holder,
        TaskCompletionSource<IUnitOfWork> completion)
    {
        try
        {
            await uow.BeginTransactionAsync();
            completion.TrySetResult(uow);
        }
        catch (Exception initializationError)
        {
            if (ReferenceEquals(holder.UnitOfWork, uow))
            {
                holder.UnitOfWork = null;
                holder.Initialization = null;
            }

            try
            {
                uow.Dispose();
            }
            catch (Exception cleanupError)
            {
                initializationError.Data[CleanupExceptionDataKey] = cleanupError;
            }

            completion.TrySetException(initializationError);
        }
    }

    public void ClearCurrent() => ClearAmbientState();

    private static void ClearAmbientState()
    {
        if (_current.Value is { } holder)
        {
            holder.UnitOfWork = null;
            holder.Initialization = null;
        }

        _current.Value = null;
    }

    /// <summary>
    /// Chỉ dùng cho test cleanup (mở qua InternalsVisibleTo). `_current` là AsyncLocal *tĩnh*
    /// dùng chung cho mọi instance UnitOfWorkManager trong process — nếu một test fail giữa
    /// chừng và bỏ lỡ ClearCurrent(), trạng thái cũ có thể rò rỉ sang test kế tiếp. Test base
    /// gọi hàm này trong Dispose() (luôn chạy dù test pass/fail) để đảm bảo sạch tuyệt đối.
    /// </summary>
    internal static void ResetAmbientStateForTests() => ClearAmbientState();
}
