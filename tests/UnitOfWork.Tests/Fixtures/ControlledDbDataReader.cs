using System.Collections;
using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;

namespace UnitOfWork.Tests.Fixtures;

internal sealed class ControlledDbDataReader : DbDataReader, IDbColumnSchemaGenerator
{
    private readonly TaskCompletionSource? _readGate;
    private readonly Exception? _readException;
    private readonly Exception? _disposeException;
    private readonly Exception? _disposeAsyncException;
    private readonly Exception? _closeAsyncException;
    private int _isClosed;

    public ControlledDbDataReader(
        TaskCompletionSource? readGate = null,
        Exception? readException = null,
        Exception? disposeException = null,
        Exception? disposeAsyncException = null,
        Exception? closeAsyncException = null)
    {
        _readGate = readGate;
        _readException = readException;
        _disposeException = disposeException;
        _disposeAsyncException = disposeAsyncException;
        _closeAsyncException = closeAsyncException;
    }

    public int ReadCount { get; private set; }
    public int ReadAsyncCount { get; private set; }
    public int NextResultCount { get; private set; }
    public int NextResultAsyncCount { get; private set; }
    public int CloseCount { get; private set; }
    public int DisposeCount { get; private set; }
    public int DisposeAsyncCount { get; private set; }
    public int CloseAsyncCount { get; private set; }
    public int GetFieldValueAsyncCount { get; private set; }
    public int IsDBNullAsyncCount { get; private set; }
    public int GetSchemaTableAsyncCount { get; private set; }
    public int GetColumnSchemaAsyncCount { get; private set; }
    public CancellationToken LastReadCancellationToken { get; private set; }
    public CancellationToken LastNextResultCancellationToken { get; private set; }
    public CancellationToken LastGetFieldValueCancellationToken { get; private set; }
    public CancellationToken LastIsDBNullCancellationToken { get; private set; }
    public CancellationToken LastGetSchemaTableCancellationToken { get; private set; }
    public CancellationToken LastGetColumnSchemaCancellationToken { get; private set; }
    public int LastGetFieldValueOrdinal { get; private set; }
    public int LastIsDBNullOrdinal { get; private set; }
    public bool ThrowOnSynchronousApis { get; init; }
    public object FieldValueAsyncResult { get; init; } = 37;
    public bool IsDBNullAsyncResult { get; init; } = true;
    public DataTable SchemaTableAsyncResult { get; } = new("AsyncSchema");
    public ReadOnlyCollection<DbColumn> ColumnSchemaAsyncResult { get; } =
        new(Array.Empty<DbColumn>());

    public override int Depth => 0;
    public override int FieldCount => 1;
    public override bool HasRows => true;
    public override bool IsClosed => Volatile.Read(ref _isClosed) != 0;
    public override int RecordsAffected => 0;
    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool GetBoolean(int ordinal) => false;
    public override byte GetByte(int ordinal) => 0;
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;
    public override char GetChar(int ordinal) => '\0';
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;
    public override string GetDataTypeName(int ordinal) => "String";
    public override DateTime GetDateTime(int ordinal) => DateTime.UnixEpoch;
    public override decimal GetDecimal(int ordinal) => 0;
    public override double GetDouble(int ordinal) => 0;
    public override IEnumerator GetEnumerator() => Array.Empty<object>().GetEnumerator();
    public override Type GetFieldType(int ordinal) => typeof(string);
    public override float GetFloat(int ordinal) => 0;
    public override Guid GetGuid(int ordinal) => Guid.Empty;
    public override short GetInt16(int ordinal) => 0;
    public override int GetInt32(int ordinal) => 0;
    public override long GetInt64(int ordinal) => 0;
    public override string GetName(int ordinal) => "Value";
    public override int GetOrdinal(string name) => 0;
    public override string GetString(int ordinal) => string.Empty;
    public override object GetValue(int ordinal)
    {
        ThrowIfSynchronousApisAreForbidden();
        return string.Empty;
    }

    public override T GetFieldValue<T>(int ordinal)
    {
        ThrowIfSynchronousApisAreForbidden();
        return (T)GetValue(ordinal);
    }

    public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken)
    {
        GetFieldValueAsyncCount++;
        LastGetFieldValueOrdinal = ordinal;
        LastGetFieldValueCancellationToken = cancellationToken;
        return Task.FromResult((T)FieldValueAsyncResult);
    }
    public override int GetValues(object[] values)
    {
        if (values.Length > 0)
            values[0] = string.Empty;

        return values.Length > 0 ? 1 : 0;
    }

    public override bool IsDBNull(int ordinal)
    {
        ThrowIfSynchronousApisAreForbidden();
        return false;
    }

    public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken)
    {
        IsDBNullAsyncCount++;
        LastIsDBNullOrdinal = ordinal;
        LastIsDBNullCancellationToken = cancellationToken;
        return Task.FromResult(IsDBNullAsyncResult);
    }

    public override DataTable GetSchemaTable()
    {
        ThrowIfSynchronousApisAreForbidden();
        return SchemaTableAsyncResult;
    }

    public override Task<DataTable?> GetSchemaTableAsync(CancellationToken cancellationToken = default)
    {
        GetSchemaTableAsyncCount++;
        LastGetSchemaTableCancellationToken = cancellationToken;
        return Task.FromResult<DataTable?>(SchemaTableAsyncResult);
    }

    public ReadOnlyCollection<DbColumn> GetColumnSchema()
    {
        ThrowIfSynchronousApisAreForbidden();
        return ColumnSchemaAsyncResult;
    }

    public override Task<ReadOnlyCollection<DbColumn>> GetColumnSchemaAsync(
        CancellationToken cancellationToken = default)
    {
        GetColumnSchemaAsyncCount++;
        LastGetColumnSchemaCancellationToken = cancellationToken;
        return Task.FromResult(ColumnSchemaAsyncResult);
    }

    public override void Close()
    {
        ThrowIfSynchronousApisAreForbidden();
        CloseCount++;
        Interlocked.Exchange(ref _isClosed, 1);
    }

    public override Task CloseAsync()
    {
        CloseAsyncCount++;
        Interlocked.Exchange(ref _isClosed, 1);
        return _closeAsyncException is null
            ? Task.CompletedTask
            : Task.FromException(_closeAsyncException);
    }

    public override bool NextResult()
    {
        NextResultCount++;
        return false;
    }

    public override async Task<bool> NextResultAsync(CancellationToken cancellationToken)
    {
        NextResultAsyncCount++;
        LastNextResultCancellationToken = cancellationToken;
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }

    public override bool Read()
    {
        ReadCount++;
        _readGate?.Task.GetAwaiter().GetResult();
        if (_readException is not null)
            throw _readException;

        return false;
    }

    public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        ReadAsyncCount++;
        LastReadCancellationToken = cancellationToken;
        if (_readGate is not null)
            await _readGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        if (_readException is not null)
            throw _readException;

        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeCount++;
            Interlocked.Exchange(ref _isClosed, 1);
            if (_disposeException is not null)
                throw _disposeException;
        }

        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        DisposeAsyncCount++;
        Interlocked.Exchange(ref _isClosed, 1);
        if (_disposeAsyncException is not null)
            return ValueTask.FromException(_disposeAsyncException);

        return ValueTask.CompletedTask;
    }

    private void ThrowIfSynchronousApisAreForbidden()
    {
        if (ThrowOnSynchronousApis)
            throw new InvalidOperationException("Synchronous provider reader API was used.");
    }
}
