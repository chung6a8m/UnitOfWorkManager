using RepoDb.Interfaces;
using UnitOfWork.Core;

namespace UnitOfWork.RepoDb.ProviderTests.Shared;

public interface IRepoDbProviderHarness : IAsyncDisposable
{
    string ProviderName { get; }

    string ScalarOneSql { get; }

    IDbHelper GetOfficialHelper();

    void RegisterMappings(IDbHelper helper);

    IDbConnectionFactory CreateConnectionFactory();

    Task ResetSchemaAsync(CancellationToken cancellationToken = default);

    Task<long> CountRowsAsync(
        string tableName,
        CancellationToken cancellationToken = default);
}
