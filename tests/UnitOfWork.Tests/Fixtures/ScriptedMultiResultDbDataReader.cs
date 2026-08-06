using System.Collections;
using System.Data.Common;
using System.Globalization;
using static UnitOfWork.Tests.Fixtures.ScriptedMultiResultDbConnection;

namespace UnitOfWork.Tests.Fixtures;

internal sealed class ScriptedMultiResultDbDataReader : DbDataReader
{
    private readonly IReadOnlyList<IReadOnlyList<object?[]>> _resultSets;
    private readonly ScriptedFixtureState _fixture;
    private bool _blockNextRead;
    private int _resultSetIndex;
    private int _rowIndex = -1;
    private bool _closed;

    public ScriptedMultiResultDbDataReader(
        IReadOnlyList<IReadOnlyList<object?[]>> resultSets,
        ScriptedFixtureState fixture,
        bool blockNextRead)
    {
        _resultSets = resultSets;
        _fixture = fixture;
        _blockNextRead = blockNextRead;
    }

    public override int FieldCount => CurrentRows.Count == 0
        ? 1
        : CurrentRows[0].Length;

    public override bool HasRows => CurrentRows.Count > 0;

    public override bool IsClosed => _closed;

    public override int RecordsAffected => -1;

    public override int Depth => 0;

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    private IReadOnlyList<object?[]> CurrentRows =>
        _resultSetIndex < _resultSets.Count
            ? _resultSets[_resultSetIndex]
            : [];

    public override bool Read()
    {
        ThrowIfClosed();
        _rowIndex++;
        return _rowIndex < CurrentRows.Count;
    }

    public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        ThrowIfClosed();

        if (_blockNextRead)
        {
            _blockNextRead = false;
            _fixture.ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                .ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Read();
    }

    public override bool NextResult()
    {
        ThrowIfClosed();
        if (_resultSetIndex + 1 >= _resultSets.Count)
            return false;

        _resultSetIndex++;
        _rowIndex = -1;
        return true;
    }

    public override Task<bool> NextResultAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(NextResult());
    }

    public override string GetName(int ordinal) => $"Column{ordinal}";

    public override int GetOrdinal(string name)
    {
        if (name.StartsWith("Column", StringComparison.Ordinal) &&
            int.TryParse(name.AsSpan("Column".Length), out var ordinal))
        {
            return ordinal;
        }

        return 0;
    }

    public override Type GetFieldType(int ordinal)
    {
        var value = CurrentRows.Count > 0
            ? CurrentRows[0][ordinal]
            : null;
        return value?.GetType() ?? typeof(object);
    }

    public override string GetDataTypeName(int ordinal) =>
        GetFieldType(ordinal).Name;

    public override object GetValue(int ordinal)
    {
        ThrowIfClosed();
        if (_rowIndex < 0 || _rowIndex >= CurrentRows.Count)
            throw new InvalidOperationException("The reader is not positioned on a row.");

        return CurrentRows[_rowIndex][ordinal] ?? DBNull.Value;
    }

    public override int GetValues(object[] values)
    {
        var count = Math.Min(values.Length, FieldCount);
        for (var index = 0; index < count; index++)
            values[index] = GetValue(index);
        return count;
    }

    public override bool IsDBNull(int ordinal) => GetValue(ordinal) is DBNull;

    public override Task<bool> IsDBNullAsync(
        int ordinal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(IsDBNull(ordinal));
    }

    public override T GetFieldValue<T>(int ordinal)
    {
        var value = GetValue(ordinal);
        if (value is T typed)
            return typed;

        return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }

    public override Task<T> GetFieldValueAsync<T>(
        int ordinal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GetFieldValue<T>(ordinal));
    }

    public override bool GetBoolean(int ordinal) => GetFieldValue<bool>(ordinal);

    public override byte GetByte(int ordinal) => GetFieldValue<byte>(ordinal);

    public override long GetBytes(
        int ordinal,
        long dataOffset,
        byte[]? buffer,
        int bufferOffset,
        int length)
    {
        var source = GetFieldValue<byte[]>(ordinal);
        var available = Math.Max(0, source.Length - (int)dataOffset);
        var count = Math.Min(length, available);
        if (buffer is not null && count > 0)
            Array.Copy(source, dataOffset, buffer, bufferOffset, count);
        return count;
    }

    public override char GetChar(int ordinal) => GetFieldValue<char>(ordinal);

    public override long GetChars(
        int ordinal,
        long dataOffset,
        char[]? buffer,
        int bufferOffset,
        int length)
    {
        var source = GetFieldValue<string>(ordinal).ToCharArray();
        var available = Math.Max(0, source.Length - (int)dataOffset);
        var count = Math.Min(length, available);
        if (buffer is not null && count > 0)
            Array.Copy(source, dataOffset, buffer, bufferOffset, count);
        return count;
    }

    public override DateTime GetDateTime(int ordinal) => GetFieldValue<DateTime>(ordinal);

    public override decimal GetDecimal(int ordinal) => GetFieldValue<decimal>(ordinal);

    public override double GetDouble(int ordinal) => GetFieldValue<double>(ordinal);

    public override float GetFloat(int ordinal) => GetFieldValue<float>(ordinal);

    public override Guid GetGuid(int ordinal) => GetFieldValue<Guid>(ordinal);

    public override short GetInt16(int ordinal) => GetFieldValue<short>(ordinal);

    public override int GetInt32(int ordinal) => GetFieldValue<int>(ordinal);

    public override long GetInt64(int ordinal) => GetFieldValue<long>(ordinal);

    public override string GetString(int ordinal) => GetFieldValue<string>(ordinal);

    public override IEnumerator GetEnumerator()
    {
        while (Read())
            yield return this;
    }

    public override void Close() => _closed = true;

    protected override void Dispose(bool disposing)
    {
        _closed = true;
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        _closed = true;
        return ValueTask.CompletedTask;
    }

    private void ThrowIfClosed()
    {
        if (_closed)
            throw new InvalidOperationException("The reader is closed.");
    }
}
