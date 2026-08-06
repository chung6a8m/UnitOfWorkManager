using System.Data;
using System.Data.Common;
using System.Runtime.ExceptionServices;
using UnitOfWork.Core.Exceptions;

namespace UnitOfWork.Core;

/// <summary>
/// UnitOfWork dùng ref-counting: mọi BeginAsync() lồng nhau trong cùng flow
/// đều tái sử dụng chính instance này (không savepoint). Đi kèm 2 lớp guard:
///  1) EnsureSameLogicalFlow — chặn truy cập từ flow AsyncLocal khác.
///  2) GuardedExecuteAsync   — chặn 2 thao tác chạy đồng thời trên cùng UoW.
/// </summary>
public class UnitOfWork : IUnitOfWork
{
    private readonly Func<Type, IDbConnection, IDbTransaction?, object> _repositoryFactory;
    private readonly Dictionary<Type, object> _repositories = new();

    private sealed class AmbientFlowHolder
    {
        public Guid? FlowId;
    }

    // Định danh flow đã tạo ra UoW này. Dùng holder mutable để việc cleanup ở
    // async callee cũng được quan sát bởi execution context của caller.
    private readonly Guid _ownerFlowId;
    private static readonly AsyncLocal<AmbientFlowHolder?> AmbientFlow = new();

    internal static Guid? AmbientFlowId
    {
        get => AmbientFlow.Value?.FlowId;
        set
        {
            if (value.HasValue)
            {
                AmbientFlow.Value = new AmbientFlowHolder { FlowId = value };
                return;
            }

            if (AmbientFlow.Value is { } current)
                current.FlowId = null;

            AmbientFlow.Value = null;
        }
    }

    // 0 = rảnh, 1 = đang có thao tác chạy — check-and-set nguyên tử qua Interlocked.
    private int _operationInProgress;
    private int _refCount = 1;
    private bool _rollbackRequested;
    private bool _isDisposed;

    public IDbConnection Connection { get; }
    public IDbTransaction? Transaction { get; private set; }

    public Guid OwnerFlowId => _ownerFlowId;
    internal int RefCount => _refCount;
    internal bool IsDisposed => _isDisposed;
    internal bool RollbackRequested => _rollbackRequested;

    public UnitOfWork(
        IDbConnection connection,
        Func<Type, IDbConnection, IDbTransaction?, object> repositoryFactory)
    {
        Connection = connection;
        _repositoryFactory = repositoryFactory;
        _ownerFlowId = Guid.NewGuid();
        AmbientFlowId = _ownerFlowId;
    }

    public async Task BeginTransactionAsync()
    {
        if (Connection.State != ConnectionState.Open)
        {
            if (Connection is DbConnection dbConn)
                await dbConn.OpenAsync();
            else
                Connection.Open();
        }

        Transaction = Connection.BeginTransaction();
    }

    public void IncrementRef()
    {
        EnsureSameLogicalFlow();
        Interlocked.Increment(ref _refCount);
    }

    private void EnsureSameLogicalFlow()
    {
        var ambient = AmbientFlowId;

        if (ambient == null)
        {
            throw new UnitOfWorkConcurrencyException(
                $"UnitOfWork (owner={_ownerFlowId}) bị truy cập từ execution context không thấy owner id " +
                "(AsyncLocal rỗng). Khả năng cao: một task chạy nền (Task.Run, ExecutionContext.SuppressFlow, " +
                "message consumer...) đang giữ tham chiếu trực tiếp tới UnitOfWork của flow khác thay vì tự " +
                "gọi BeginAsync() để tạo UnitOfWork riêng.");
        }

        if (ambient != _ownerFlowId)
        {
            throw new UnitOfWorkConcurrencyException(
                $"UnitOfWork (owner={_ownerFlowId}) bị truy cập từ flow khác (ambient={ambient}). " +
                "Mỗi luồng logic (request/job) phải dùng UnitOfWork của chính nó, không share instance.");
        }
    }

    private async Task<T> GuardedExecuteAsync<T>(Func<Task<T>> operation)
    {
        EnsureNotDisposed();
        EnsureSameLogicalFlow();

        if (Interlocked.CompareExchange(ref _operationInProgress, 1, 0) != 0)
        {
            throw new UnitOfWorkConcurrencyException(
                $"UnitOfWork (owner={_ownerFlowId}) đang bị gọi đồng thời bởi 2 thao tác khác nhau. " +
                "IDbConnection/IDbTransaction không thread-safe — kiểm tra Task.WhenAll/Parallel.ForEach " +
                "có đang dùng chung UoW này giữa các nhánh song song không.");
        }

        try
        {
            return await operation();
        }
        finally
        {
            Interlocked.Exchange(ref _operationInProgress, 0);
        }
    }

    private Task GuardedExecuteAsync(Func<Task> operation) =>
        GuardedExecuteAsync(async () => { await operation(); return true; });

    // Expose cho GuardedDbCommand (cùng assembly) tái sử dụng guard đồng thời.
    internal Task<T> RunGuardedAsync<T>(Func<Task<T>> op) => GuardedExecuteAsync(op);

    private void EnsureNotDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(UnitOfWork),
                $"UnitOfWork (owner={_ownerFlowId}) đã Dispose — có thể một task nền đang cầm tham chiếu " +
                "tới UoW đã đóng của request trước.");
        }
    }

    public T GetRepository<T>() where T : class
    {
        EnsureNotDisposed();
        EnsureSameLogicalFlow();

        var type = typeof(T);
        if (!_repositories.TryGetValue(type, out var repo))
        {
            var guardedConnection = new GuardedDbConnection(Connection, this);
            repo = _repositoryFactory(type, guardedConnection, Transaction);
            _repositories[type] = repo;
        }
        return (T)repo;
    }

    public async Task CommitAsync()
    {
        await GuardedExecuteAsync(async () =>
        {
            if (Interlocked.Decrement(ref _refCount) > 0) return true;

            if (_rollbackRequested) Transaction?.Rollback();
            else Transaction?.Commit();

            return true;
        });
    }

    public async Task RollbackAsync()
    {
        await GuardedExecuteAsync(async () =>
        {
            _rollbackRequested = true;
            if (Interlocked.Decrement(ref _refCount) > 0) return true;

            Transaction?.Rollback();
            return true;
        });
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Exception? transactionDisposalError = null;
        Exception? connectionDisposalError = null;

        try
        {
            Transaction?.Dispose();
        }
        catch (Exception ex)
        {
            transactionDisposalError = ex;
        }

        try
        {
            Connection.Dispose();
        }
        catch (Exception ex)
        {
            connectionDisposalError = ex;
        }
        finally
        {
            if (AmbientFlowId == _ownerFlowId)
                AmbientFlowId = null;
        }

        if (transactionDisposalError is not null && connectionDisposalError is not null)
        {
            throw new AggregateException(
                "Không thể dispose transaction và connection của UnitOfWork.",
                transactionDisposalError,
                connectionDisposalError);
        }

        var disposalError = transactionDisposalError ?? connectionDisposalError;
        if (disposalError is not null)
            ExceptionDispatchInfo.Capture(disposalError).Throw();
    }
}
