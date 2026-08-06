using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using static UnitOfWork.Tests.Fixtures.ScriptedMultiResultDbConnection;

namespace UnitOfWork.Tests.Fixtures;

internal sealed class ScriptedMultiResultDbCommand : DbCommand
{
    private readonly ScriptedFixtureState _fixture;
    private readonly ScriptedDbParameterCollection _parameters = new();
    private DbConnection? _connection;
    private DbTransaction? _transaction;

    public ScriptedMultiResultDbCommand(
        DbConnection connection,
        ScriptedFixtureState fixture)
    {
        _connection = connection;
        _fixture = fixture;
    }

    [AllowNull]
    public override string CommandText { get; set; } = string.Empty;

    public override int CommandTimeout { get; set; } = 30;

    public override CommandType CommandType { get; set; } = CommandType.Text;

    public override UpdateRowSource UpdatedRowSource { get; set; }

    public override bool DesignTimeVisible { get; set; }

    protected override DbConnection? DbConnection
    {
        get => _connection;
        set => _connection = value;
    }

    protected override DbParameterCollection DbParameterCollection => _parameters;

    protected override DbTransaction? DbTransaction
    {
        get => _transaction;
        set => _transaction = value;
    }

    public override void Cancel()
    {
    }

    public override int ExecuteNonQuery() => 1;

    public override object? ExecuteScalar() =>
        string.Equals(CommandText, "scalar-one", StringComparison.Ordinal)
            ? 1
            : null;

    public override Task<int> ExecuteNonQueryAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ExecuteNonQuery());
    }

    public override Task<object?> ExecuteScalarAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ExecuteScalar());
    }

    public override void Prepare()
    {
    }

    protected override DbParameter CreateDbParameter() => new ScriptedDbParameter();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
        CreateReader();

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<DbDataReader>(CreateReader());
    }

    private ScriptedMultiResultDbDataReader CreateReader()
    {
        if (!string.Equals(CommandText, "script", StringComparison.Ordinal))
            return new ScriptedMultiResultDbDataReader([], _fixture, blockNextRead: false);

        var blockNextRead = _fixture.BlockNextRead;
        _fixture.BlockNextRead = false;
        return new ScriptedMultiResultDbDataReader(
            _fixture.ResultSets,
            _fixture,
            blockNextRead);
    }

    private sealed class ScriptedDbParameter : DbParameter
    {
        public override DbType DbType { get; set; }

        public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;

        public override bool IsNullable { get; set; }

        [AllowNull]
        public override string ParameterName { get; set; } = string.Empty;

        public override int Size { get; set; }

        [AllowNull]
        public override string SourceColumn { get; set; } = string.Empty;

        public override bool SourceColumnNullMapping { get; set; }

        public override DataRowVersion SourceVersion { get; set; } = DataRowVersion.Current;

        public override object? Value { get; set; }

        public override void ResetDbType()
        {
        }
    }

    private sealed class ScriptedDbParameterCollection : DbParameterCollection
    {
        private readonly List<DbParameter> _items = [];

        public override int Count => _items.Count;

        public override bool IsFixedSize => false;

        public override bool IsReadOnly => false;

        public override bool IsSynchronized => false;

        public override object SyncRoot => ((ICollection)_items).SyncRoot;

        public override int Add(object value)
        {
            _items.Add((DbParameter)value);
            return _items.Count - 1;
        }

        public override void AddRange(Array values)
        {
            foreach (var value in values)
                Add(value!);
        }

        public override void Clear() => _items.Clear();

        public override bool Contains(object value) => _items.Contains((DbParameter)value);

        public override bool Contains(string value) => IndexOf(value) >= 0;

        public override void CopyTo(Array array, int index) =>
            ((ICollection)_items).CopyTo(array, index);

        public override IEnumerator GetEnumerator() => _items.GetEnumerator();

        public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);

        public override int IndexOf(string parameterName) =>
            _items.FindIndex(parameter => string.Equals(
                parameter.ParameterName,
                parameterName,
                StringComparison.Ordinal));

        public override void Insert(int index, object value) =>
            _items.Insert(index, (DbParameter)value);

        public override void Remove(object value) => _items.Remove((DbParameter)value);

        public override void RemoveAt(int index) => _items.RemoveAt(index);

        public override void RemoveAt(string parameterName)
        {
            var index = IndexOf(parameterName);
            if (index >= 0)
                RemoveAt(index);
        }

        protected override DbParameter GetParameter(int index) => _items[index];

        protected override DbParameter GetParameter(string parameterName)
        {
            var index = IndexOf(parameterName);
            if (index < 0)
                throw new IndexOutOfRangeException(parameterName);

            return _items[index];
        }

        protected override void SetParameter(int index, DbParameter value) =>
            _items[index] = value;

        protected override void SetParameter(string parameterName, DbParameter value)
        {
            var index = IndexOf(parameterName);
            if (index < 0)
                _items.Add(value);
            else
                _items[index] = value;
        }
    }
}
