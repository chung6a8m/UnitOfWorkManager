using System.Collections.Concurrent;
using System.Data;
using RepoDb;
using RepoDb.Interfaces;

namespace UnitOfWork.RepoDb.ProviderTests.Shared;

public sealed class CountingDbHelper : IDbHelper
{
    private readonly IDbHelper _inner;
    private readonly ConcurrentDictionary<string, int> _counts =
        new(StringComparer.OrdinalIgnoreCase);

    public CountingDbHelper(IDbHelper inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public IResolver<string, Type> DbTypeResolver => _inner.DbTypeResolver;

    public int GetInvocationCount(string tableName) =>
        _counts.TryGetValue(Normalize(tableName), out var count) ? count : 0;

    public IEnumerable<DbField> GetFields(
        IDbConnection connection,
        string tableName,
        IDbTransaction? transaction = null)
    {
        Increment(tableName);
        return _inner.GetFields(connection, tableName, transaction!);
    }

    public async Task<IEnumerable<DbField>> GetFieldsAsync(
        IDbConnection connection,
        string tableName,
        IDbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        Increment(tableName);
        return await _inner.GetFieldsAsync(
            connection,
            tableName,
            transaction!,
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

    private void Increment(string tableName) =>
        _counts.AddOrUpdate(Normalize(tableName), 1, static (_, count) => count + 1);

    private static string Normalize(string tableName)
    {
        var normalized = tableName.Trim().Trim('"', '`', '[', ']');
        var separator = normalized.LastIndexOf('.');
        if (separator >= 0)
            normalized = normalized[(separator + 1)..];
        return normalized.ToLowerInvariant();
    }
}
