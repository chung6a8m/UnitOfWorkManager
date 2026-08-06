using System.Data;

namespace UnitOfWork.Core;

public sealed record UnitOfWorkOptions
{
    public IsolationLevel? IsolationLevel { get; init; }
    public int? CommandTimeoutSeconds { get; init; }
    public TimeSpan? TransactionTimeout { get; init; }
    public bool ReadOnly { get; init; }

    internal UnitOfWorkOptions Validate()
    {
        if (CommandTimeoutSeconds is <= 0)
            throw new ArgumentOutOfRangeException(nameof(CommandTimeoutSeconds));
        if (TransactionTimeout is { } timeout && timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(TransactionTimeout));
        return this;
    }
}
