using System.Data;
using RepoDb;
using RepoDb.Interfaces;

namespace UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests.Fixtures;

internal sealed class InjectedDbHelper : IDbHelper
{
    private readonly IDbHelper _inner;

    public InjectedDbHelper(IDbHelper inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public Func<IDbConnection, string, IDbTransaction?, CancellationToken, Task>?
        BeforeGetFieldsAsync { get; init; }

    public IResolver<string, Type> DbTypeResolver => _inner.DbTypeResolver;

    public IEnumerable<DbField> GetFields(
        IDbConnection connection,
        string tableName,
        IDbTransaction? transaction = null) =>
        _inner.GetFields(connection, tableName, transaction);

    public async Task<IEnumerable<DbField>> GetFieldsAsync(
        IDbConnection connection,
        string tableName,
        IDbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        if (BeforeGetFieldsAsync is not null)
        {
            await BeforeGetFieldsAsync(
                connection,
                tableName,
                transaction,
                cancellationToken).ConfigureAwait(false);
        }

        return await _inner.GetFieldsAsync(
            connection,
            tableName,
            transaction,
            cancellationToken).ConfigureAwait(false);
    }

    public T GetScopeIdentity<T>(
        IDbConnection connection,
        IDbTransaction? transaction = null) =>
        _inner.GetScopeIdentity<T>(connection, transaction!);

    public Task<T> GetScopeIdentityAsync<T>(
        IDbConnection connection,
        IDbTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        _inner.GetScopeIdentityAsync<T>(
            connection,
            transaction!,
            cancellationToken);

    public void DynamicHandler<TEventInstance>(TEventInstance instance, string key) =>
        _inner.DynamicHandler(instance, key);
}
