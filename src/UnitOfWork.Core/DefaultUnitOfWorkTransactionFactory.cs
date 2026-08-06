using System.Data.Common;

namespace UnitOfWork.Core;

internal sealed class DefaultUnitOfWorkTransactionFactory : IUnitOfWorkTransactionFactory
{
    public ValueTask<DbTransaction> BeginTransactionAsync(
        DbConnection connection,
        UnitOfWorkOptions options,
        CancellationToken cancellationToken)
    {
        if (options.ReadOnly || options.TransactionTimeout is not null)
        {
            throw new NotSupportedException(
                "Read-only and transaction-timeout options require a provider-specific transaction factory.");
        }

        return options.IsolationLevel is { } isolationLevel
            ? connection.BeginTransactionAsync(isolationLevel, cancellationToken)
            : connection.BeginTransactionAsync(cancellationToken);
    }
}
