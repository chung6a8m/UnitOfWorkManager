using System.Data.Common;

namespace UnitOfWork.Core;

public interface IUnitOfWorkTransactionFactory
{
    ValueTask<DbTransaction> BeginTransactionAsync(
        DbConnection connection,
        UnitOfWorkOptions options,
        CancellationToken cancellationToken);
}
