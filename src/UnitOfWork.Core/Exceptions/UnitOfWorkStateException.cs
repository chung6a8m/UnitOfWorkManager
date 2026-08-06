namespace UnitOfWork.Core.Exceptions;

public sealed class UnitOfWorkStateException : InvalidOperationException
{
    public UnitOfWorkStateException(string message)
        : base(message)
    {
    }
}
