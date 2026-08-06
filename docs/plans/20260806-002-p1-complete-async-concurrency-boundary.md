# P1 — Hoàn thiện async và concurrency boundary — Implementation Plan

> **Dành cho agent triển khai:** BẮT BUỘC dùng `superpowers:subagent-driven-development` (khuyến nghị) hoặc `superpowers:executing-plans` để thực hiện lần lượt từng task. Dùng checkbox `- [ ]` để theo dõi tiến độ.

**Goal:** Hoàn thiện P1 trên baseline P0 hiện tại bằng ADO.NET async thật, cancellation xuyên suốt, `IAsyncDisposable`, operation lease giữ suốt vòng đời reader, repository cache an toàn khi truy cập đồng thời và transaction options có extension point rõ ràng.

**Architecture:** Chuẩn hóa public/provider boundary từ `IDb*` sang `DbConnection`/`DbTransaction`/`DbCommand`/`DbDataReader` để mọi async method có thể gọi trực tiếp provider async API. `RootUnitOfWork` cấp một atomic `UnitOfWorkOperationLease`; command giữ lease trong thời gian execute, còn reader nhận chuyển giao lease và chỉ release khi `Close`/`Dispose`/`DisposeAsync`. Root vẫn là owner duy nhất của transaction/resource; scope hỗ trợ cả sync dispose compatibility và async cleanup được khuyến nghị.

**Tech Stack:** .NET 8, C# latest, `System.Data.Common`, `AsyncLocal<T>`, `Interlocked`, `CancellationToken`, `IAsyncDisposable`, xUnit 2.9.2, Microsoft.Data.Sqlite 8.0.10, PowerShell.

## Global Constraints

- Baseline là `main` sau khi P0 đã tạo `RootUnitOfWork`, distinct `UnitOfWorkScope`, transaction-bound facades, instance-owned ambient state và deterministic lifecycle state machine.
- Chỉ triển khai các mục P1 trong `docs/repository-strengths-and-weaknesses.md`: true async, cancellation, async disposal, reader-lifetime guard, repository/lifecycle concurrency policy, transaction options và failure policy.
- Không quay lại public raw connection/transaction; public connection vẫn là transaction-bound facade.
- Đây là breaking API change có chủ đích từ `IDbConnection` sang `DbConnection`. Không giữ overload/factory shim dùng `IDbConnection` sau migration cuối.
- Sync command APIs vẫn hoạt động; async APIs phải gọi provider async APIs trực tiếp. Cấm fake async bằng `Task.FromResult`, `Task.Run` hoặc async lambda chứa I/O sync.
- Mọi internal `await` trong library phải dùng `ConfigureAwait(false)` trừ khi API trả thẳng `ValueTask` không cần await.
- Mọi I/O async có API token phải nhận và truyền đúng `CancellationToken`: open, begin transaction, prepare, execute, read/next-result thông qua provider reader, commit và rollback.
- `DisposeAsync()` không nhận token và phải dùng cleanup không hủy được để tránh bỏ dở rollback/dispose.
- Chỉ một database operation lease được active trên một root. Vi phạm phải fail-fast bằng `UnitOfWorkConcurrencyException`; không queue ngầm và không chạy song song trên cùng connection/transaction.
- Reader giữ operation lease từ lúc `ExecuteReader*` bắt đầu thành công cho tới `Close`, `Dispose` hoặc `DisposeAsync`. Lỗi tạo reader phải release lease ngay.
- `CommandBehavior.CloseConnection` phải bị từ chối vì connection lifecycle thuộc root.
- `Cancel()` của command được phép forward trực tiếp, không acquire operation lease, để một flow khác có thể yêu cầu provider hủy operation đang chạy.
- Repository cache phải trả đúng một instance cho mỗi repository type trong một root và không được corrupt khi hai inherited child task gọi `GetRepository<T>()` đồng thời.
- Repository factory là synchronous object factory và không được thực hiện database I/O; README phải ghi rõ constraint này.
- Finalization vẫn fail-fast khi operation lease đang active. Scope không được settle nếu bị từ chối; caller có thể dispose reader/chờ command xong rồi retry completion.
- Scope settle đúng một lần. Commit/rollback cancellation hoặc failure sau khi finalization đã bắt đầu làm root `Faulted`, outcome không được retry.
- Nếu commit/rollback đã thành công nhưng cleanup lỗi, giữ completion outcome đã biết, chuyển lifecycle sang `Faulted`, clear ambient và throw cleanup error.
- Nếu commit/rollback lỗi hoặc bị hủy, completion outcome là `Faulted`; primary error được giữ nguyên, cleanup error gắn tại `Exception.Data["UnitOfWorkCleanupException"]`.
- Incomplete `DisposeAsync()` phải request rollback rõ ràng; không dựa vào provider tự rollback khi transaction dispose.
- `UnitOfWorkOptions` là immutable snapshot theo root. Nested begin chỉ được dùng options bằng options root; options khác phải fail trước khi acquire scope.
- Core không thêm dependency SQL Server/PostgreSQL. Provider-specific read-only/transaction-timeout behavior đi qua `IUnitOfWorkTransactionFactory`; default factory chỉ hỗ trợ isolation level và fail-fast với option không hỗ trợ.
- Không triển khai savepoint, automatic retry, distributed transaction, connection pooling policy, DbBatch wrapper, provider matrix CI, package metadata hoặc benchmark; các nội dung đó thuộc follow-up P2.
- Test concurrency phải dùng `TaskCompletionSource`/barrier, không dùng delay để tạo overlap.
- File Markdown dùng UTF-8 và checkout CRLF theo `.editorconfig`/`.gitattributes`.

---

## Public API đích

```csharp
using System.Data.Common;

namespace UnitOfWork.Core;

public interface IUnitOfWorkContext
{
    DbConnection Connection { get; }
    T GetRepository<T>() where T : class;
}

public interface IUnitOfWorkScope : IUnitOfWorkContext, IDisposable, IAsyncDisposable
{
    Task CompleteAsync(CancellationToken cancellationToken = default);
    Task RollbackAsync(CancellationToken cancellationToken = default);
}
```

```csharp
using System.Data.Common;

namespace UnitOfWork.Core;

public interface IUnitOfWorkManager
{
    IUnitOfWorkContext Current { get; }
    bool HasCurrent { get; }

    Task<IUnitOfWorkScope> BeginAsync(
        UnitOfWorkOptions? options = null,
        CancellationToken cancellationToken = default);
}

public interface IDbConnectionFactory
{
    DbConnection CreateConnection();
}
```

```csharp
using System.Data;

namespace UnitOfWork.Core;

public sealed record UnitOfWorkOptions
{
    public IsolationLevel? IsolationLevel { get; init; }
    public int? CommandTimeoutSeconds { get; init; }
    public TimeSpan? TransactionTimeout { get; init; }
    public bool ReadOnly { get; init; }
}
```

```csharp
using System.Data.Common;

namespace UnitOfWork.Core;

public interface IUnitOfWorkTransactionFactory
{
    ValueTask<DbTransaction> BeginTransactionAsync(
        DbConnection connection,
        UnitOfWorkOptions options,
        CancellationToken cancellationToken);
}
```

Constructor cuối cùng:

```csharp
public UnitOfWorkManager(
    IDbConnectionFactory connectionFactory,
    Func<Type, DbConnection, object> repositoryFactory,
    IUnitOfWorkTransactionFactory? transactionFactory = null)
```

Cách dùng async được khuyến nghị:

```csharp
await using var scope = await manager.BeginAsync(
    new UnitOfWorkOptions
    {
        IsolationLevel = IsolationLevel.ReadCommitted,
        CommandTimeoutSeconds = 30
    },
    cancellationToken);

await using var command = scope.Connection.CreateCommand();
command.CommandText = "UPDATE Counters SET Value = Value + 1 WHERE Id = 1;";
await command.ExecuteNonQueryAsync(cancellationToken);

await scope.CompleteAsync(cancellationToken);
```

Streaming reader giữ độc quyền root operation cho tới khi dispose:

```csharp
await using var command = scope.Connection.CreateCommand();
command.CommandText = "SELECT Value FROM Counters ORDER BY Id;";

await using var reader = await command.ExecuteReaderAsync(cancellationToken);
while (await reader.ReadAsync(cancellationToken))
{
    Consume(reader.GetInt32(0));
}
```

## P1 invariants bắt buộc

| ID | Invariant | Cơ chế cưỡng chế |
|---|---|---|
| P1-I01 | Async API không gọi provider sync API. | Các facade kế thừa `Db*` và override async trực tiếp. |
| P1-I02 | Cancellation token đi tới provider. | Token được forward tại open/begin/prepare/execute/commit/rollback. |
| P1-I03 | Reader giữ guard suốt lifetime. | `TransactionBoundDbDataReader` sở hữu `UnitOfWorkOperationLease`. |
| P1-I04 | Reader release lease đúng một lần. | Atomic dispose state trong reader wrapper. |
| P1-I05 | Lỗi/cancel khi tạo reader không leak gate. | `try/catch` dispose lease trước khi rethrow. |
| P1-I06 | Finalization không overlap operation/reader. | Root kiểm tra active lease trước khi settle scope cuối. |
| P1-I07 | Repository cache không race. | Lifecycle/cache lock và create-once semantics. |
| P1-I08 | Async dispose rollback rõ ràng. | Active scope `DisposeAsync()` settle `Abandoned`. |
| P1-I09 | Finalization failure/cancel không retry. | `Active -> Finalizing -> Faulted` terminal transition. |
| P1-I10 | Cleanup luôn clear ambient. | Cleanup callback trong non-cancelable `finally`. |
| P1-I11 | Nested options không thay đổi root transaction. | Normalize + equality check trước `AcquireScope()`. |
| P1-I12 | Provider-specific transaction config có extension point. | `IUnitOfWorkTransactionFactory`. |
| P1-I13 | Connection ownership không bị bypass qua async API. | Facade chặn open/close/begin/dispose sync và async. |
| P1-I14 | `CommandBehavior.CloseConnection` không đóng raw connection. | Fail-fast trước provider call. |
| P1-I15 | Inherited task chỉ được dùng root tuần tự. | Ambient current-root check + single operation lease. |

## Cancellation và failure policy

| Tình huống | Hành vi bắt buộc |
|---|---|
| Caller hủy khi chờ shared initialization | Scope reservation chưa activate được release. Nếu không còn reservation, root hủy initialization và cleanup. |
| Một nested caller hủy nhưng caller khác vẫn chờ | Chỉ nested call đó fail; shared initialization tiếp tục cho reservation còn lại. |
| Execute/prepare bị hủy | Provider exception đi ra; operation lease release; scope vẫn active để caller rollback/dispose. |
| Reader creation bị hủy | Không trả reader; lease release ngay. |
| Reader `ReadAsync` bị hủy | Reader vẫn sở hữu lease cho tới khi caller dispose reader. |
| Token đã canceled trước `CompleteAsync`/`RollbackAsync` | Throw trước khi mutate scope/root; scope vẫn active. |
| Commit/rollback bị hủy sau khi finalization bắt đầu | Root `Faulted`, non-retriable, cleanup và clear ambient; `OperationCanceledException` là primary. |
| Commit/rollback lỗi | Root `Faulted`, non-retriable, cleanup và clear ambient; provider error là primary. |
| Commit/rollback thành công, cleanup lỗi | Giữ outcome `Committed`/`RolledBack`, lifecycle `Faulted`, clear ambient và throw cleanup error. |
| `DisposeAsync()` trên scope chưa complete | Request rollback bằng `CancellationToken.None`; outermost scope thực hiện rollback async rõ ràng. |
| Finalization khi reader/command lease còn active | Fail-fast, scope vẫn active và chưa giảm active-scope count. |

## File map

**Create**

```text
src/UnitOfWork.Core/UnitOfWorkOptions.cs
src/UnitOfWork.Core/IUnitOfWorkTransactionFactory.cs
src/UnitOfWork.Core/DefaultUnitOfWorkTransactionFactory.cs
src/UnitOfWork.Core/UnitOfWorkOperationLease.cs
src/UnitOfWork.Core/TransactionBoundDbDataReader.cs
tests/UnitOfWork.Tests/AsyncAdoNetTests.cs
tests/UnitOfWork.Tests/ReaderLifetimeGuardTests.cs
tests/UnitOfWork.Tests/RepositoryCacheConcurrencyTests.cs
tests/UnitOfWork.Tests/UnitOfWorkOptionsTests.cs
tests/UnitOfWork.Tests/Fixtures/AsyncOnlyDbConnection.cs
tests/UnitOfWork.Tests/Fixtures/ControlledDbDataReader.cs
scripts/verify-p1-async-concurrency-boundary.ps1
```

**Modify**

```text
src/UnitOfWork.Core/IUnitOfWorkContext.cs
src/UnitOfWork.Core/IUnitOfWorkManager.cs
src/UnitOfWork.Core/IUnitOfWorkScope.cs
src/UnitOfWork.Core/RootUnitOfWork.cs
src/UnitOfWork.Core/UnitOfWorkManager.cs
src/UnitOfWork.Core/UnitOfWorkScope.cs
src/UnitOfWork.Core/TransactionBoundDbConnection.cs
src/UnitOfWork.Core/TransactionBoundDbCommand.cs
src/UnitOfWork.Core/TransactionBoundDbTransaction.cs
tests/UnitOfWork.Tests/Fixtures/ControlledDbConnection.cs
tests/UnitOfWork.Tests/Fixtures/CounterRepository.cs
tests/UnitOfWork.Tests/AsyncFlowIsolationTests.cs
tests/UnitOfWork.Tests/CommitRollbackIntegrationTests.cs
tests/UnitOfWork.Tests/ConcurrencyGuardTests.cs
tests/UnitOfWork.Tests/DisposalTests.cs
tests/UnitOfWork.Tests/ManagerIsolationTests.cs
tests/UnitOfWork.Tests/ScopeLifecycleTests.cs
tests/UnitOfWork.Tests/TransactionInvariantTests.cs
README.md
```

**Delete sau migration**

```text
Không xóa source file P0. Chỉ xóa helper/overload test tạm dùng `IDbConnection` nếu được tạo trong quá trình migration.
```

---

### Task 1: Chuẩn hóa provider boundary sang `System.Data.Common` và thêm transaction options

**Files:** Tạo `UnitOfWorkOptions.cs`, `IUnitOfWorkTransactionFactory.cs`, `DefaultUnitOfWorkTransactionFactory.cs`, `UnitOfWorkOptionsTests.cs`; sửa public interfaces, manager/root, ba transaction-bound facade và fixtures để dùng `Db*`.

**Interfaces:**

- Consumes: P0 root/scope lifecycle và automatic transaction binding.
- Produces: `DbConnection` public facade, `UnitOfWorkOptions`, `IUnitOfWorkTransactionFactory`, options snapshot trên root.

- [ ] **Step 1: Viết contract tests fail cho `Db*` API và options validation**

Các `[Fact]` bắt buộc:

```text
Public_Context_Exposes_DbConnection_Facade
Connection_Factory_Returns_DbConnection
BeginAsync_Uses_Requested_Isolation_Level
Command_Timeout_Option_Is_Applied_To_Every_New_Command
Nested_Begin_With_Different_Options_Is_Rejected_Before_Acquiring_Scope
Invalid_Command_Timeout_Is_Rejected_Before_Connection_Creation
Invalid_Transaction_Timeout_Is_Rejected_Before_Connection_Creation
Default_Transaction_Factory_Rejects_ReadOnly_And_TransactionTimeout
Custom_Transaction_Factory_Receives_Full_Options
```

Test option mismatch phải giữ root nguyên vẹn:

```csharp
[Fact]
public async Task Nested_Begin_With_Different_Options_Is_Rejected_Before_Acquiring_Scope()
{
    var connection = new ControlledDbConnection(initiallyOpen: true);
    var manager = CreateManager(connection);
    var outerOptions = new UnitOfWorkOptions
    {
        IsolationLevel = IsolationLevel.Serializable,
        CommandTimeoutSeconds = 15
    };

    await using var outer = await manager.BeginAsync(outerOptions);

    await Assert.ThrowsAsync<UnitOfWorkStateException>(() => manager.BeginAsync(
        outerOptions with { CommandTimeoutSeconds = 30 }));

    Assert.Equal(1, GetRoot(manager).ActiveScopeCount);
    await outer.RollbackAsync();
}
```

- [ ] **Step 2: Chạy test để xác nhận fail**

```powershell
dotnet test tests/UnitOfWork.Tests/UnitOfWork.Tests.csproj --filter FullyQualifiedName~UnitOfWorkOptionsTests
```

Expected: compile failure vì public contracts và options types chưa tồn tại.

- [ ] **Step 3: Tạo immutable options và validation cố định**

```csharp
public sealed record UnitOfWorkOptions
{
    public IsolationLevel? IsolationLevel { get; init; }
    public int? CommandTimeoutSeconds { get; init; }
    public TimeSpan? TransactionTimeout { get; init; }
    public bool ReadOnly { get; init; }

    internal UnitOfWorkOptions Validate()
    {
        if (CommandTimeoutSeconds is <= 0)
            throw new ArgumentOutOfRangeException(nameof(CommandTimeoutSeconds));
        if (TransactionTimeout is { } timeout && timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(TransactionTimeout));
        return this;
    }
}
```

Manager phải normalize `null` thành một instance mặc định duy nhất theo root. Record equality được dùng để kiểm tra nested options vì toàn bộ property đều có value equality.

- [ ] **Step 4: Tạo transaction factory mặc định và extension point**

```csharp
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
```

`IUnitOfWorkTransactionFactory` là public để SQL Server/PostgreSQL adapter có thể áp dụng `SET TRANSACTION`, timeout hoặc provider option mà core không phụ thuộc package provider.

- [ ] **Step 5: Migrate contracts và facades sang `Db*`**

Thay các signatures cuối cùng:

```csharp
DbConnection IUnitOfWorkContext.Connection { get; }
DbConnection IDbConnectionFactory.CreateConnection();
Func<Type, DbConnection, object> repositoryFactory;
DbConnection _connection;
DbTransaction? _transaction;
```

Ba facade phải kế thừa `DbConnection`, `DbCommand`, `DbTransaction`. Ở task này giữ sync behavior hiện có để migration compile; async override hoàn chỉnh ở Task 2.

`TransactionBoundDbConnection` phải chặn cả ownership APIs mới xuất hiện qua `DbConnection`: `OpenAsync`, `CloseAsync`, `BeginTransactionAsync`, `DisposeAsync`; `CreateBatch`/`DbBatch` phải fail-fast `NotSupportedException` hoặc `UnitOfWorkStateException` vì chưa có transaction-bound batch wrapper.

- [ ] **Step 6: Lưu options trên root và áp dụng command timeout**

`RootUnitOfWork` constructor nhận `UnitOfWorkOptions options` và `IUnitOfWorkTransactionFactory transactionFactory`. `CreateTransactionBoundCommand()` áp dụng:

```csharp
if (Options.CommandTimeoutSeconds is { } timeout)
    command.CommandTimeout = timeout;
```

Nested `BeginAsync()` so sánh requested normalized options với `currentRoot.Options` trước `AcquireScope()`.

- [ ] **Step 7: Migrate fixtures/tests và chạy full suite**

`ControlledConnectionFactory.CreateConnection()` trả `DbConnection`; `CounterRepository` nhận `DbConnection`. Không để cast từ `IDbConnection` sang `DbConnection` trong production code.

```powershell
dotnet test tests/UnitOfWork.Tests/UnitOfWork.Tests.csproj --filter FullyQualifiedName~UnitOfWorkOptionsTests
dotnet test UnitOfWork.slnx --no-restore
```

- [ ] **Step 8: Commit**

```bash
git add src/UnitOfWork.Core tests/UnitOfWork.Tests
git commit -m "refactor: standardize unit of work on db common contracts"
```

---

### Task 2: Thay boolean guard bằng operation lease và triển khai command async thật

**Files:** Tạo `UnitOfWorkOperationLease.cs`, `AsyncAdoNetTests.cs`, `Fixtures/AsyncOnlyDbConnection.cs`; sửa `RootUnitOfWork.cs`, `TransactionBoundDbCommand.cs`, `ControlledDbConnection.cs`, `ConcurrencyGuardTests.cs`.

**Interfaces:**

- Consumes: `DbCommand` facade và root lifecycle lock từ Task 1.
- Produces: `RootUnitOfWork.EnterOperation(string)`, idempotent `UnitOfWorkOperationLease`, sync/async command paths dùng cùng gate.

- [ ] **Step 1: Viết async-only provider fixture**

`AsyncOnlyDbConnection` và inner transaction/command phải:

- override sync `Open`, `BeginDbTransaction`, `Commit`, `Rollback`, `Execute*`, `Prepare` để throw `InvalidOperationException("Synchronous provider API was used.")`;
- override async counterpart để tăng counter, nhận token và trả controlled result;
- expose counters `OpenAsyncCount`, `BeginTransactionAsyncCount`, `ExecuteNonQueryAsyncCount`, `ExecuteScalarAsyncCount`, `PrepareAsyncCount`, `CommitAsyncCount`, `RollbackAsyncCount`, `DisposeAsyncCount`;
- có `TaskCompletionSource` gate cho execute/cancellation tests.

- [ ] **Step 2: Viết fail tests cho true async và token propagation**

Các `[Fact]` bắt buộc:

```text
Open_And_Begin_Use_Async_Provider_APIs
ExecuteNonQueryAsync_Uses_Provider_Async_API
ExecuteScalarAsync_Uses_Provider_Async_API
PrepareAsync_Uses_Provider_Async_API
Cancellation_Token_Is_Forwarded_To_Command
Canceled_Command_Releases_Operation_Lease
Sync_Command_Still_Uses_Provider_Sync_API
Overlapping_Async_Commands_Fail_Fast_Deterministically
Command_Cancel_Can_Reach_Active_Provider_Operation
```

Test overlap dùng barrier:

```csharp
var first = command1.ExecuteScalarAsync(CancellationToken.None);
await provider.OperationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

await Assert.ThrowsAsync<UnitOfWorkConcurrencyException>(
    () => command2.ExecuteScalarAsync(CancellationToken.None));

provider.ReleaseOperation();
await first;
```

- [ ] **Step 3: Tạo atomic operation lease**

```csharp
internal sealed class UnitOfWorkOperationLease : IDisposable
{
    private RootUnitOfWork? _owner;

    internal UnitOfWorkOperationLease(RootUnitOfWork owner) => _owner = owner;

    public void Dispose() =>
        Interlocked.Exchange(ref _owner, null)?.ReleaseOperation();
}
```

Root API cố định:

```csharp
internal UnitOfWorkOperationLease EnterOperation(string operationName);
internal void ReleaseOperation();
internal bool HasActiveOperation { get; }
internal string? ActiveOperationName { get; }
```

`EnterOperation` phải lock cùng lifecycle lock, gọi current-root/active-state check, rồi `CompareExchange` flag. Exception message gồm operation đang giữ lease và operation bị từ chối.

- [ ] **Step 4: Rewrite sync command methods không fake async**

```csharp
public override int ExecuteNonQuery()
{
    using var lease = _owner.EnterOperation(nameof(ExecuteNonQuery));
    return _inner.ExecuteNonQuery();
}

public override object? ExecuteScalar()
{
    using var lease = _owner.EnterOperation(nameof(ExecuteScalar));
    return _inner.ExecuteScalar();
}
```

`Prepare()` dùng cùng pattern. Xóa toàn bộ `Task.FromResult(...).GetAwaiter().GetResult()`.

- [ ] **Step 5: Implement async command methods trực tiếp**

```csharp
public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
{
    using var lease = _owner.EnterOperation(nameof(ExecuteNonQueryAsync));
    return await _inner.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
}

public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
{
    using var lease = _owner.EnterOperation(nameof(ExecuteScalarAsync));
    return await _inner.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
}

public override async Task PrepareAsync(CancellationToken cancellationToken = default)
{
    using var lease = _owner.EnterOperation(nameof(PrepareAsync));
    await _inner.PrepareAsync(cancellationToken).ConfigureAwait(false);
}
```

`Cancel()` chỉ forward `_inner.Cancel()`; không acquire gate. `Dispose(bool)` và `DisposeAsync()` chỉ dispose command caller-owned, không thay đổi root lease nếu command không có active reader.

- [ ] **Step 6: Chạy targeted tests và full suite**

```powershell
dotnet test tests/UnitOfWork.Tests/UnitOfWork.Tests.csproj --filter "FullyQualifiedName~AsyncAdoNetTests|FullyQualifiedName~ConcurrencyGuardTests"
dotnet test UnitOfWork.slnx --no-restore
```

- [ ] **Step 7: Commit**

```bash
git add src/UnitOfWork.Core tests/UnitOfWork.Tests
git commit -m "feat: add true async command operation leases"
```

---

### Task 3: Giữ operation lease suốt vòng đời `DbDataReader`

**Files:** Tạo `TransactionBoundDbDataReader.cs`, `ReaderLifetimeGuardTests.cs`, `Fixtures/ControlledDbDataReader.cs`; sửa `TransactionBoundDbCommand.cs`, controlled fixtures và transaction invariant tests.

**Interfaces:**

- Consumes: `UnitOfWorkOperationLease` từ Task 2.
- Produces: reader wrapper release-once và command reader sync/async transfer lease.

- [ ] **Step 1: Tạo controlled reader fixture**

Fixture phải hỗ trợ:

- sync/async `Read` và `NextResult` counters;
- dispose/async-dispose counters;
- tùy chọn throw khi create, read, dispose hoặc async dispose;
- `TaskCompletionSource` để giữ reader open trong concurrency test;
- đủ delegate members để `DbDataReader` wrapper test bằng xUnit mà không dùng mock framework.

- [ ] **Step 2: Viết reader lifetime tests fail**

Các `[Fact]` bắt buộc:

```text
Reader_Holds_Operation_Lease_Until_Dispose
Reader_Holds_Operation_Lease_Until_DisposeAsync
Reader_Close_Releases_Operation_Lease
Reader_Dispose_Releases_Lease_Even_When_Inner_Dispose_Throws
Reader_DisposeAsync_Releases_Lease_Even_When_Inner_DisposeAsync_Throws
Reader_Creation_Failure_Releases_Operation_Lease
Reader_Creation_Cancellation_Releases_Operation_Lease
ReadAsync_Uses_Provider_Async_API_And_Forwards_Token
NextResultAsync_Uses_Provider_Async_API_And_Forwards_Token
Completion_While_Reader_Is_Open_Is_Rejected_Without_Settling_Scope
CommandBehavior_CloseConnection_Is_Rejected_Before_Provider_Call
```

Test chính:

```csharp
[Fact]
public async Task Reader_Holds_Operation_Lease_Until_DisposeAsync()
{
    await using var scope = await manager.BeginAsync();
    await using var readerCommand = scope.Connection.CreateCommand();
    await using var overlappingCommand = scope.Connection.CreateCommand();

    var reader = await readerCommand.ExecuteReaderAsync();

    await Assert.ThrowsAsync<UnitOfWorkConcurrencyException>(
        () => overlappingCommand.ExecuteScalarAsync());
    await Assert.ThrowsAsync<UnitOfWorkConcurrencyException>(
        () => scope.CompleteAsync());

    await reader.DisposeAsync();
    await overlappingCommand.ExecuteScalarAsync();
    await scope.RollbackAsync();
}
```

- [ ] **Step 3: Implement reader wrapper với release-once state**

Skeleton bắt buộc:

```csharp
public sealed class TransactionBoundDbDataReader : DbDataReader
{
    private readonly DbDataReader _inner;
    private UnitOfWorkOperationLease? _lease;
    private int _closed;

    internal TransactionBoundDbDataReader(
        DbDataReader inner,
        UnitOfWorkOperationLease lease)
    {
        _inner = inner;
        _lease = lease;
    }

    public override void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;

        try { _inner.Close(); }
        finally { ReleaseLease(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing || Interlocked.Exchange(ref _closed, 1) != 0)
            return;

        try { _inner.Dispose(); }
        finally { ReleaseLease(); }
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;

        try { await _inner.DisposeAsync().ConfigureAwait(false); }
        finally { ReleaseLease(); }
    }

    private void ReleaseLease() =>
        Interlocked.Exchange(ref _lease, null)?.Dispose();
}
```

Delegate toàn bộ abstract properties/getters, indexers, typed getters, `GetEnumerator`, `Read`, `ReadAsync`, `NextResult`, `NextResultAsync` tới inner. `Read*`/`NextResult*` không acquire lease mới vì reader đã giữ lease.

- [ ] **Step 4: Transfer lease từ command sang reader**

```csharp
protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
{
    RejectCloseConnection(behavior);
    var lease = _owner.EnterOperation(nameof(ExecuteReader));
    try
    {
        var reader = _inner.ExecuteReader(behavior);
        return new TransactionBoundDbDataReader(reader, lease);
    }
    catch
    {
        lease.Dispose();
        throw;
    }
}
```

Async path tương tự nhưng gọi `await _inner.ExecuteReaderAsync(behavior, cancellationToken).ConfigureAwait(false)`. Sau khi wrapper được tạo, command không dispose lease; reader là owner mới.

- [ ] **Step 5: Chạy tests và commit**

```powershell
dotnet test tests/UnitOfWork.Tests/UnitOfWork.Tests.csproj --filter "FullyQualifiedName~ReaderLifetimeGuardTests|FullyQualifiedName~TransactionInvariantTests|FullyQualifiedName~ConcurrencyGuardTests"
git add src/UnitOfWork.Core tests/UnitOfWork.Tests
git commit -m "fix: guard unit of work for full reader lifetime"
```

---

### Task 4: Hoàn thiện async initialization/finalization, cancellation và `IAsyncDisposable`

**Files:** Sửa `RootUnitOfWork.cs`, `UnitOfWorkManager.cs`, `UnitOfWorkScope.cs`, `IUnitOfWorkScope.cs`, `IUnitOfWorkManager.cs`, `ControlledDbConnection.cs`, `AsyncAdoNetTests.cs`, `DisposalTests.cs`, `ScopeLifecycleTests.cs`, `ManagerIsolationTests.cs`.

**Interfaces:**

- Consumes: transaction factory, options snapshot, operation lease.
- Produces: true async open/begin/commit/rollback/cleanup, scope async disposal, shared-initialization cancellation policy.

- [ ] **Step 1: Mở rộng controlled transaction/resource counters**

Thêm async overrides/counters:

```text
BeginTransactionAsyncCount
CommitAsyncCount
RollbackAsyncCount
TransactionDisposeAsyncCount
ConnectionDisposeAsyncCount
LastOpenCancellationToken
LastBeginCancellationToken
LastCommitCancellationToken
LastRollbackCancellationToken
```

Cho phép gate và exception riêng cho open, begin, commit, rollback, transaction dispose và connection dispose. Sync counter phải độc lập để test chứng minh async path không gọi sync fallback.

- [ ] **Step 2: Viết lifecycle async/cancellation tests fail**

Các `[Fact]` bắt buộc:

```text
BeginAsync_Uses_OpenAsync_And_BeginTransactionAsync
Outermost_Complete_Uses_CommitAsync
Outermost_Rollback_Uses_RollbackAsync
DisposeAsync_Without_Completion_Explicitly_Rolls_Back
DisposeAsync_Attempts_Transaction_And_Connection_Async_Disposal
Nested_DisposeAsync_Requests_Rollback_Without_Disposing_Root
PreCanceled_Complete_Leaves_Scope_Active
PreCanceled_Rollback_Leaves_Scope_Active
Commit_Cancellation_Faults_Root_And_Is_Not_Retried
Rollback_Cancellation_Faults_Root_And_Is_Not_Retried
Commit_Success_With_Cleanup_Failure_Preserves_Committed_Outcome
Rollback_Success_With_Cleanup_Failure_Preserves_RolledBack_Outcome
Initialization_Cancellation_Clears_Ambient_And_Allows_Fresh_Begin
Canceled_Nested_Wait_Releases_Only_Its_Scope_Reservation
Last_Canceled_Initialization_Wait_Cancels_Root_Initialization
```

- [ ] **Step 3: Dùng provider async APIs trong initialization**

```csharp
if (_connection.State != ConnectionState.Open)
    await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);

_transaction = await _transactionFactory.BeginTransactionAsync(
    _connection,
    Options,
    cancellationToken).ConfigureAwait(false);
```

Root sở hữu một internal initialization `CancellationTokenSource`. Mỗi begin tạo một scope reservation trước khi chờ shared initialization. Caller token chỉ cancel wait của reservation đó; root initialization CTS chỉ bị cancel khi không còn reservation nào.

Bổ sung internal path cố định:

```csharp
internal void CancelScopeBeforeActivation(UnitOfWorkScope scope);
```

Path này giảm reservation đúng một lần, không đặt rollback flag vì scope chưa được trả và chưa thể thực hiện I/O. Nếu reservation cuối biến mất trong `Initializing`, cancel root initialization CTS; initialization catch cleanup resource và clear ambient.

- [ ] **Step 4: Thêm token vào manager/scope API**

```csharp
public Task<IUnitOfWorkScope> BeginAsync(
    UnitOfWorkOptions? options = null,
    CancellationToken cancellationToken = default);

public Task CompleteAsync(CancellationToken cancellationToken = default);
public Task RollbackAsync(CancellationToken cancellationToken = default);
```

`AwaitScopeAsync` phải catch cancellation/failure trước khi scope active và gọi `CancelScopeBeforeActivation`. Không trả một scope có reservation bị hủy.

`CompleteAsync`/`RollbackAsync` gọi `cancellationToken.ThrowIfCancellationRequested()` trước khi đổi `_state`. Nếu token đã canceled, scope còn `Active`.

- [ ] **Step 5: Implement async finalization và deterministic outcome policy**

Pseudo-flow cố định:

```csharp
private async Task FinalizeAsync(
    UnitOfWorkCompletionOutcome requestedOutcome,
    CancellationToken cancellationToken)
{
    Exception? primaryError = null;
    try
    {
        if (requestedOutcome == UnitOfWorkCompletionOutcome.RolledBack)
            await _transaction!.RollbackAsync(cancellationToken).ConfigureAwait(false);
        else
            await _transaction!.CommitAsync(cancellationToken).ConfigureAwait(false);

        Volatile.Write(ref _completionOutcome, (int)requestedOutcome);
    }
    catch (Exception error)
    {
        primaryError = error;
        Volatile.Write(ref _completionOutcome, (int)UnitOfWorkCompletionOutcome.Faulted);
        throw;
    }
    finally
    {
        try
        {
            await DisposeResourcesAsync().ConfigureAwait(false);
        }
        catch (Exception cleanupError) when (primaryError is not null)
        {
            AddCleanupException(primaryError, cleanupError);
        }
        finally
        {
            _onRootFinished();
        }
    }
}
```

Implementation cuối phải set lifecycle `Disposed` chỉ khi finalization và cleanup đều thành công. Cleanup failure sau known commit/rollback set lifecycle `Faulted` nhưng không đổi known completion outcome.

- [ ] **Step 6: Implement non-cancelable async cleanup và sync compatibility**

`DisposeResourcesAsync()` phải gọi transaction rồi connection, tiếp tục resource thứ hai nếu resource đầu lỗi và aggregate khi cả hai lỗi.

```csharp
public ValueTask DisposeAsync() =>
    new(DisposeAsyncCore());

public void Dispose() =>
    DisposeAsync().AsTask().ConfigureAwait(false).GetAwaiter().GetResult();
```

`DisposeAsyncCore()` trên active scope settle `Abandoned` bằng `CancellationToken.None`; dispose sau settled là idempotent. README đánh dấu `Dispose()` là compatibility fallback; async application dùng `await using`.

- [ ] **Step 7: Chạy failure matrix và full suite**

```powershell
dotnet test tests/UnitOfWork.Tests/UnitOfWork.Tests.csproj --filter "FullyQualifiedName~AsyncAdoNetTests|FullyQualifiedName~DisposalTests|FullyQualifiedName~ScopeLifecycleTests|FullyQualifiedName~ManagerIsolationTests"
dotnet test UnitOfWork.slnx --no-restore
```

- [ ] **Step 8: Commit**

```bash
git add src/UnitOfWork.Core tests/UnitOfWork.Tests
git commit -m "feat: complete async unit of work lifecycle"
```

---

### Task 5: Khóa repository cache và xác định execution-flow concurrency policy

**Files:** Tạo `RepositoryCacheConcurrencyTests.cs`; sửa `RootUnitOfWork.cs`, `AsyncFlowIsolationTests.cs`, `ConcurrencyGuardTests.cs`, `README.md` phần policy nháp.

**Interfaces:**

- Consumes: root lifecycle lock và operation lease.
- Produces: create-once repository cache, deterministic behavior cho inherited/suppressed/background flow.

- [ ] **Step 1: Viết repository cache tests fail**

Các `[Fact]` bắt buộc:

```text
Concurrent_GetRepository_Returns_One_Cached_Instance
Concurrent_GetRepository_Invokes_Factory_Exactly_Once
Repository_Factory_Failure_Does_Not_Cache_Partial_Instance
Repository_Factory_Can_Be_Retried_After_Failure
GetRepository_During_Finalization_Is_Rejected
GetRepository_From_Suppressed_Flow_Is_Rejected
```

Factory-count test dùng barrier trước khi release factory để bảo đảm hai task thực sự overlap; không assert bằng delay.

- [ ] **Step 2: Serialize cache access dưới lifecycle boundary**

`GetRepository<T>()` phải lock `_lifecycleLock`, gọi một `EnsureUsableNoLock()` không acquire lock lần hai, rồi create/cache atomically:

```csharp
internal T GetRepository<T>() where T : class
{
    lock (_lifecycleLock)
    {
        EnsureUsableNoLock();
        var type = typeof(T);
        if (_repositories.TryGetValue(type, out var existing))
            return (T)existing;

        var created = _repositoryFactory(type, ConnectionNoCheck);
        _repositories.Add(type, created);
        return (T)created;
    }
}
```

Chỉ add sau khi factory trả thành công. Factory exception không để key/value rác. Không dùng `ConcurrentDictionary.GetOrAdd` vì value factory có thể chạy nhiều lần.

- [ ] **Step 3: Giữ lifecycle và operation checks cùng một lock order**

Lock order duy nhất:

```text
_scope settlement lock -> root lifecycle lock -> operation flag
```

Không code path nào acquire theo thứ tự ngược. Repository factory không được await và không được thực hiện I/O; document đây là constructor-only factory.

- [ ] **Step 4: Chốt flow policy bằng executable tests**

Các `[Fact]` bắt buộc:

```text
Inherited_Task_Can_Use_Current_Root_Sequentially
Inherited_Tasks_Cannot_Execute_Commands_Concurrently
Retained_Scope_After_Root_Finalization_Is_Rejected
Suppressed_Flow_Cannot_Use_Retained_Scope
Background_Task_Still_Running_Blocks_Finalization_Until_Operation_Ends
```

Policy cuối:

- `AsyncLocal` inherited child task nhìn thấy cùng root;
- sequential use khi root active được phép;
- overlap bị fail-fast bởi operation lease;
- suppressed execution context bị current-root check từ chối;
- retained scope sau finalization bị state exception;
- library không tuyên bố cấm mọi `Task.Run`; library cấm parallel/shared operation và usage ngoài active lifecycle.

- [ ] **Step 5: Chạy tests lặp và commit**

```powershell
dotnet test tests/UnitOfWork.Tests/UnitOfWork.Tests.csproj --filter "FullyQualifiedName~RepositoryCacheConcurrencyTests|FullyQualifiedName~AsyncFlowIsolationTests|FullyQualifiedName~ConcurrencyGuardTests"
1..3 | ForEach-Object {
    dotnet test UnitOfWork.slnx --no-build --logger "console;verbosity=minimal"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
git add src/UnitOfWork.Core tests/UnitOfWork.Tests README.md
git commit -m "fix: serialize repository creation within root lifecycle"
```

---

### Task 6: Cập nhật tài liệu, compatibility evidence và local verification

**Files:** Sửa `README.md`; tạo `scripts/verify-p1-async-concurrency-boundary.ps1`; hoàn thiện SQLite async integration tests trong `AsyncAdoNetTests.cs`/`ReaderLifetimeGuardTests.cs`.

- [ ] **Step 1: Thêm SQLite async integration evidence**

Bắt buộc có test dùng `Microsoft.Data.Sqlite` thật:

```text
Sqlite_Async_Insert_And_Complete_Commits_Data
Sqlite_Async_Insert_And_Rollback_Discards_Data
Sqlite_Async_Reader_Blocks_Second_Command_Until_Disposed
Sqlite_Async_Cancellation_Leaves_Scope_Usable_For_Rollback
```

Test đọc kết quả bằng connection độc lập. Không dùng SQLite cùng connection để giả lập overlap provider; concurrency guard phải bị kích hoạt trước provider call.

- [ ] **Step 2: Cập nhật README public contract và migration table**

README phải có:

- `await using` example với options và cancellation;
- sync compatibility example;
- streaming reader lifetime rule;
- fail-fast `Task.WhenAll` anti-pattern trên cùng scope;
- inherited task policy chính xác;
- repository factory phải sync/I/O-free;
- `CommandBehavior.CloseConnection` và `DbBatch` không hỗ trợ;
- default transaction factory chỉ hỗ trợ isolation level;
- cách inject provider-specific transaction factory cho read-only/transaction timeout;
- failure/cancellation table tóm tắt outcome và retry policy.

Migration table:

| Before P1 | After P1 |
|---|---|
| `IDbConnection` public/factory | `DbConnection` public/factory |
| `Func<Type, IDbConnection, object>` | `Func<Type, DbConnection, object>` |
| `BeginAsync()` | `BeginAsync(options, cancellationToken)` |
| scope chỉ `IDisposable` | scope `IDisposable` + `IAsyncDisposable` |
| command async không có | `DbCommand.Execute*Async` thật |
| reader trả raw provider reader | transaction-bound reader giữ operation lease |
| provider default transaction config | `UnitOfWorkOptions` + transaction factory |

- [ ] **Step 3: Tạo verification script**

```powershell
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

Push-Location $repoRoot
try {
    dotnet restore UnitOfWork.slnx
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    dotnet build UnitOfWork.slnx --no-restore --warnaserror
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    1..3 | ForEach-Object {
        dotnet test UnitOfWork.slnx --no-build --logger "console;verbosity=minimal"
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    $sourceFiles = Get-ChildItem src/UnitOfWork.Core -Recurse -File -Include *.cs
    $forbiddenPatterns = @(
        'TransactionBoundDbConnection\s*:\s*IDbConnection',
        'TransactionBoundDbCommand\s*:\s*IDbCommand',
        'TransactionBoundDbTransaction\s*:\s*IDbTransaction',
        'Func<Type,\s*IDbConnection,\s*object>',
        'Task\.FromResult\s*\(\s*_inner\.Execute',
        '_connection\.BeginTransaction\s*\(',
        '_transaction\??\.Commit\s*\(',
        '_transaction\??\.Rollback\s*\('
    )

    foreach ($pattern in $forbiddenPatterns) {
        $matches = $sourceFiles | Select-String -Pattern $pattern
        if ($matches) {
            $matches | ForEach-Object { Write-Host $_.ToString() }
            throw "Forbidden P1 pattern remains: $pattern"
        }
    }

    $requiredSymbols = @(
        'IAsyncDisposable',
        'UnitOfWorkOperationLease',
        'TransactionBoundDbDataReader',
        'ExecuteNonQueryAsync',
        'BeginTransactionAsync',
        'CommitAsync',
        'RollbackAsync',
        'UnitOfWorkOptions',
        'IUnitOfWorkTransactionFactory'
    )

    foreach ($symbol in $requiredSymbols) {
        if (-not ($sourceFiles | Select-String -SimpleMatch $symbol)) {
            throw "Required P1 symbol is missing: $symbol"
        }
    }

    Write-Host 'P1 async and concurrency boundary verification passed.'
}
finally {
    Pop-Location
}
```

- [ ] **Step 4: Run verification**

```powershell
powershell -ExecutionPolicy Bypass -File scripts/verify-p1-async-concurrency-boundary.ps1
```

Expected final line:

```text
P1 async and concurrency boundary verification passed.
```

- [ ] **Step 5: Commit**

```bash
git add README.md tests/UnitOfWork.Tests scripts/verify-p1-async-concurrency-boundary.ps1
git commit -m "docs: document p1 async concurrency contract"
```

---

## Final Verification Gate

- [ ] Chạy `scripts/verify-p0-transaction-invariants.ps1` để chứng minh P1 không phá P0 invariants.
- [ ] Chạy `scripts/verify-p1-async-concurrency-boundary.ps1` từ repository root.
- [ ] Chạy full test suite ba lần liên tiếp với xUnit parallelization mặc định.
- [ ] Xác nhận `git status --short` sạch.
- [ ] Xác nhận public API dùng `DbConnection`, có `IAsyncDisposable` và token trên begin/complete/rollback.
- [ ] Xác nhận không còn fake async hoặc sync begin/commit/rollback trong root async path.
- [ ] Xác nhận reader wrapper release lease trên close, sync dispose, async dispose, creation failure và cancellation.
- [ ] Xác nhận scope completion bị từ chối khi reader còn mở nhưng scope không bị settle.
- [ ] Xác nhận concurrent repository creation trả một cached instance và factory chạy một lần.
- [ ] Xác nhận default transaction factory fail-fast với read-only/timeout và custom factory nhận đủ options.
- [ ] Xác nhận commit/rollback cancellation/failure non-retriable, ambient luôn clear và cleanup error không che primary error.
- [ ] Xác nhận `DisposeAsync()` incomplete thực hiện rollback rõ ràng trước resource disposal.

Các `[Fact]` tối thiểu phải tồn tại và chạy:

```text
Open_And_Begin_Use_Async_Provider_APIs
ExecuteNonQueryAsync_Uses_Provider_Async_API
Canceled_Command_Releases_Operation_Lease
Reader_Holds_Operation_Lease_Until_DisposeAsync
Reader_Creation_Cancellation_Releases_Operation_Lease
Completion_While_Reader_Is_Open_Is_Rejected_Without_Settling_Scope
DisposeAsync_Without_Completion_Explicitly_Rolls_Back
Commit_Cancellation_Faults_Root_And_Is_Not_Retried
Commit_Success_With_Cleanup_Failure_Preserves_Committed_Outcome
Concurrent_GetRepository_Invokes_Factory_Exactly_Once
Nested_Begin_With_Different_Options_Is_Rejected_Before_Acquiring_Scope
Custom_Transaction_Factory_Receives_Full_Options
Inherited_Tasks_Cannot_Execute_Commands_Concurrently
Retained_Scope_After_Root_Finalization_Is_Rejected
Sqlite_Async_Insert_And_Complete_Commits_Data
Sqlite_Async_Reader_Blocks_Second_Command_Until_Disposed
```

## Out of Scope Follow-up

Tạo plan P2 riêng cho GitHub Actions Windows/Linux, warnings/analyzers, coverage, NuGet metadata/license/Source Link/versioning, SQL Server/PostgreSQL provider matrix, Dapper compatibility matrix, DbBatch transaction-bound wrapper và benchmark overhead.
