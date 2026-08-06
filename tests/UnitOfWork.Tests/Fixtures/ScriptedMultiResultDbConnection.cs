using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using UnitOfWork.Core;

namespace UnitOfWork.Tests.Fixtures;

public sealed class ScriptedMultiResultDbConnection : DbConnection, IDbConnectionFactory
{
    private readonly ScriptedFixtureState _fixture;
    private readonly bool _factoryOnly;
    private ConnectionState _state;

    private ScriptedMultiResultDbConnection(ScriptedFixtureState fixture, bool factoryOnly)
    {
        _fixture = fixture;
        _factoryOnly = factoryOnly;
        _state = factoryOnly ? ConnectionState.Closed : ConnectionState.Open;
    }

    public Task ReadStarted => _fixture.ReadStarted.Task;

    public static ScriptedMultiResultDbConnection CreateBlockingReaderFixture()
    {
        var resultSets = new IReadOnlyList<object?[]>[]
        {
            new List<object?[]> { new object?[] { 42 } }
        };

        return new ScriptedMultiResultDbConnection(
            new ScriptedFixtureState(resultSets, blockNextRead: true),
            factoryOnly: true);
    }

    [AllowNull]
    public override string ConnectionString { get; set; } = "scripted";

    public override string Database => "scripted";

    public override string DataSource => "scripted";

    public override string ServerVersion => "1.0";

    public override ConnectionState State => _state;

    public override int ConnectionTimeout => 0;

    public DbConnection CreateConnection() =>
        new ScriptedMultiResultDbConnection(_fixture, factoryOnly: false);

    public override void ChangeDatabase(string databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
    }

    public override void Open()
    {
        if (_factoryOnly)
            throw new InvalidOperationException("The factory facade cannot be opened.");

        _state = ConnectionState.Open;
    }

    public override Task OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Open();
        return Task.CompletedTask;
    }

    public override void Close() => _state = ConnectionState.Closed;

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        new ScriptedDbTransaction(this, isolationLevel);

    protected override ValueTask<DbTransaction> BeginDbTransactionAsync(
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<DbTransaction>(
            new ScriptedDbTransaction(this, isolationLevel));
    }

    protected override DbCommand CreateDbCommand()
    {
        if (_factoryOnly)
            throw new InvalidOperationException("The factory facade cannot create commands.");

        return new ScriptedMultiResultDbCommand(this, _fixture);
    }

    internal sealed class ScriptedFixtureState
    {
        public ScriptedFixtureState(
            IReadOnlyList<IReadOnlyList<object?[]>> resultSets,
            bool blockNextRead)
        {
            ResultSets = resultSets;
            BlockNextRead = blockNextRead;
        }

        public IReadOnlyList<IReadOnlyList<object?[]>> ResultSets { get; }

        public bool BlockNextRead { get; set; }

        public TaskCompletionSource ReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
