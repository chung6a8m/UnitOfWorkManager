using System.Data;
using System.Data.Common;

namespace UnitOfWork.Tests.Fixtures;

internal sealed class ScriptedDbTransaction : DbTransaction
{
    private readonly DbConnection _connection;

    public ScriptedDbTransaction(DbConnection connection, IsolationLevel isolationLevel)
    {
        _connection = connection;
        IsolationLevel = isolationLevel;
    }

    public bool WasCommitted { get; private set; }

    public bool WasRolledBack { get; private set; }

    public override IsolationLevel IsolationLevel { get; }

    protected override DbConnection DbConnection => _connection;

    public override void Commit() => WasCommitted = true;

    public override Task CommitAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WasCommitted = true;
        return Task.CompletedTask;
    }

    public override void Rollback() => WasRolledBack = true;

    public override Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WasRolledBack = true;
        return Task.CompletedTask;
    }
}
