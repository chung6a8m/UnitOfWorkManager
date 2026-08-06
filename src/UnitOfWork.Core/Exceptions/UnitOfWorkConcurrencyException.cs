namespace UnitOfWork.Core.Exceptions;

/// <summary>
/// Ném ra khi một root unit of work bị truy cập từ execution flow không có current root
/// tương ứng của manager, hoặc khi hai database operation cố chạy đồng thời trên cùng root.
/// </summary>
public class UnitOfWorkConcurrencyException : Exception
{
    public UnitOfWorkConcurrencyException(string message) : base(message) { }

    public UnitOfWorkConcurrencyException(string message, Exception innerException)
        : base(message, innerException) { }
}
