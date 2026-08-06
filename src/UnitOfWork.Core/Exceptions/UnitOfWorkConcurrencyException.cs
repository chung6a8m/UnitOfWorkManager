namespace UnitOfWork.Core.Exceptions;

/// <summary>
/// Ném ra khi một <see cref="UnitOfWork.Core.UnitOfWork"/> bị truy cập:
///  - từ một "luồng logic" (AsyncLocal flow) khác với luồng đã tạo ra nó, hoặc
///  - đồng thời bởi hai thao tác cùng lúc (vi phạm tính không thread-safe của IDbConnection/IDbTransaction).
/// </summary>
public class UnitOfWorkConcurrencyException : Exception
{
    public UnitOfWorkConcurrencyException(string message) : base(message) { }

    public UnitOfWorkConcurrencyException(string message, Exception innerException)
        : base(message, innerException) { }
}
