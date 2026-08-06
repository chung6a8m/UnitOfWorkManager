# P0 — Khóa chặt transaction invariants — Implementation Plan

> **Dành cho agent triển khai:** BẮT BUỘC dùng `superpowers:subagent-driven-development` (khuyến nghị) hoặc `superpowers:executing-plans` để thực hiện lần lượt từng task. Dùng checkbox `- [ ]` để theo dõi tiến độ.

**Goal:** Loại bỏ toàn bộ đường phá transaction correctness ở mức P0: command không thể chạy ngoài transaction, nested caller không thể phá resource của outer caller, lifecycle chỉ finalize một lần, ambient tự cleanup và các manager không dùng nhầm Unit of Work của nhau.

**Architecture:** Một `RootUnitOfWork` nội bộ sở hữu raw connection, raw transaction, repository cache, operation guard và lifecycle state. Mỗi `BeginAsync()` trả một `UnitOfWorkScope` lease riêng; repository chỉ nhận `IDbConnection` facade đã bind với root transaction. Scope cuối cùng quyết định commit hoặc rollback, rồi root tự dispose resource và clear ambient.

**Tech Stack:** .NET 8, C# latest, `System.Data`, `System.Data.Common`, `AsyncLocal<T>`, `Interlocked`, xUnit 2.9.2, Microsoft.Data.Sqlite 8.0.10, PowerShell.

## Global Constraints

- Chỉ triển khai sáu hạng mục P0 trong `docs/repository-strengths-and-weaknesses.md`.
- Không triển khai savepoint, cancellation, ADO.NET async thật, `IAsyncDisposable`, reader-lifetime guard, isolation options, provider matrix, CI hoặc package work; các nội dung đó thuộc P1/P2.
- Đây là breaking API change có chủ đích. Không giữ compatibility shim cuối cùng cho `IUnitOfWork`, `CommitAsync()` hoặc `ClearCurrent()`.
- Mỗi `BeginAsync()` phải trả một scope object mới, kể cả nested begin trong lúc root đang initialization.
- Chỉ `RootUnitOfWork` được giữ raw `IDbConnection` và raw `IDbTransaction`.
- Repository factory cuối cùng là `Func<Type, IDbConnection, object>`; connection truyền vào luôn là transaction-bound facade.
- Transaction thật chỉ finalize khi active scope count về 0. Tất cả scope complete thì commit; chỉ cần một rollback hoặc dispose chưa complete thì rollback toàn bộ.
- Scope settle đúng một lần. Dispose sau settle là idempotent; dispose khi active tương đương rollback request.
- Commit/rollback/cleanup failure phải clear ambient, dispose resource đúng một lần và không retry outcome không rõ.
- Database operation và lifecycle finalization dùng chung một atomic operation gate.
- Ambient holder thuộc từng manager instance, không static.
- Giữ semantics hiện tại: initialization exception là primary; cleanup exception gắn tại `Exception.Data["UnitOfWorkCleanupException"]`.
- Test transaction phải xác nhận bằng SQLite connection độc lập hoặc controlled transaction counters.
- File Markdown dùng UTF-8 và checkout CRLF theo `.editorconfig`/`.gitattributes`.

---

## Public API đích

```csharp
using System.Data;

namespace UnitOfWork.Core;

public interface IUnitOfWorkContext
{
    IDbConnection Connection { get; }
    T GetRepository<T>() where T : class;
}

public interface IUnitOfWorkScope : IUnitOfWorkContext, IDisposable
{
    Task CompleteAsync();
    Task RollbackAsync();
}
```

```csharp
namespace UnitOfWork.Core;

public interface IUnitOfWorkManager
{
    IUnitOfWorkContext Current { get; }
    bool HasCurrent { get; }
    Task<IUnitOfWorkScope> BeginAsync();
}

public interface IDbConnectionFactory
{
    IDbConnection CreateConnection();
}
```

Constructor cuối cùng:

```csharp
public UnitOfWorkManager(
    IDbConnectionFactory connectionFactory,
    Func<Type, IDbConnection, object> repositoryFactory)
```

Cách dùng an toàn:

```csharp
using var scope = await manager.BeginAsync();
scope.GetRepository<ICounterRepository>().Insert(42);
await scope.CompleteAsync();
```

Nested scopes là các object khác nhau nhưng dùng chung một root transaction:

```csharp
private static async Task InnerAsync(IUnitOfWorkManager manager)
{
    using var inner = await manager.BeginAsync();
    inner.GetRepository<ICounterRepository>().Insert(2);
    await inner.CompleteAsync();
}
```

## Transaction invariants bắt buộc

| ID | Invariant | Cơ chế cưỡng chế |
|---|---|---|
| P0-I01 | Không public raw connection. | `TransactionBoundDbConnection`. |
| P0-I02 | Mọi command tự enlist root transaction. | `CreateCommand()` gắn transaction trước khi trả wrapper. |
| P0-I03 | Command không thể đổi/detach connection hoặc transaction. | Setter chỉ chấp nhận đúng bound facade/transaction. |
| P0-I04 | Consumer không thể commit/rollback/dispose root transaction. | `TransactionBoundDbTransaction`. |
| P0-I05 | Repository không thể open/close/dispose/change database/begin transaction. | Ownership methods ném `UnitOfWorkStateException`. |
| P0-I06 | Mỗi begin trả lease riêng. | `RootUnitOfWork.AcquireScope()`. |
| P0-I07 | Inner dispose không dispose root khi outer còn active. | Atomic active-scope count. |
| P0-I08 | Một incomplete/rollback scope ép rollback toàn bộ. | Root rollback-request flag. |
| P0-I09 | Scope không settle hai lần. | Per-scope atomic state. |
| P0-I10 | Commit/rollback chạy tối đa một lần. | Root lifecycle state machine. |
| P0-I11 | Failure không retry và luôn clear ambient. | Faulted terminal path + cleanup callback. |
| P0-I12 | Không cần public `ClearCurrent()`. | Root cleanup tự clear holder. |
| P0-I13 | Hai manager độc lập ambient. | Instance-owned `AsyncLocal`. |
| P0-I14 | Foreign/suppressed flow không dùng retained scope. | Root kiểm tra current root của manager flow. |
| P0-I15 | DB operation không overlap finalization. | Shared operation gate. |

## File map

**Create**

```text
src/UnitOfWork.Core/IUnitOfWorkContext.cs
src/UnitOfWork.Core/IUnitOfWorkScope.cs
src/UnitOfWork.Core/RootUnitOfWork.cs
src/UnitOfWork.Core/UnitOfWorkScope.cs
src/UnitOfWork.Core/UnitOfWorkLifecycleState.cs
src/UnitOfWork.Core/TransactionBoundDbConnection.cs
src/UnitOfWork.Core/TransactionBoundDbCommand.cs
src/UnitOfWork.Core/TransactionBoundDbTransaction.cs
src/UnitOfWork.Core/Exceptions/UnitOfWorkStateException.cs
tests/UnitOfWork.Tests/ScopeLifecycleTests.cs
tests/UnitOfWork.Tests/TransactionInvariantTests.cs
tests/UnitOfWork.Tests/ManagerIsolationTests.cs
scripts/verify-p0-transaction-invariants.ps1
```

**Modify**

```text
src/UnitOfWork.Core/IUnitOfWorkManager.cs
src/UnitOfWork.Core/UnitOfWorkManager.cs
src/UnitOfWork.Core/Exceptions/UnitOfWorkConcurrencyException.cs
tests/UnitOfWork.Tests/Fixtures/CounterRepository.cs
tests/UnitOfWork.Tests/Fixtures/ControlledDbConnection.cs
tests/UnitOfWork.Tests/RefCountingTests.cs
tests/UnitOfWork.Tests/CommitRollbackIntegrationTests.cs
tests/UnitOfWork.Tests/ConcurrencyGuardTests.cs
tests/UnitOfWork.Tests/AsyncFlowIsolationTests.cs
tests/UnitOfWork.Tests/DisposalTests.cs
tests/UnitOfWork.Tests/UnitOfWorkManagerAmbientTests.cs
tests/UnitOfWork.Tests/SqliteTestDbTests.cs
README.md
```

**Delete sau khi migration hoàn tất**

```text
src/UnitOfWork.Core/IUnitOfWork.cs
src/UnitOfWork.Core/UnitOfWork.cs
src/UnitOfWork.Core/GuardedDbConnection.cs
src/UnitOfWork.Core/GuardedDbCommand.cs
tests/UnitOfWork.Tests/Fixtures/UnitOfWorkTestBase.cs
tests/UnitOfWork.Tests/AssemblyInfo.cs
```

---

### Task 1: Tạo root/scope lifecycle model cạnh code legacy

**Files:** Tạo `IUnitOfWorkContext.cs`, `IUnitOfWorkScope.cs`, `UnitOfWorkLifecycleState.cs`, `UnitOfWorkStateException.cs`, `RootUnitOfWork.cs`, `UnitOfWorkScope.cs`, `ScopeLifecycleTests.cs`. Chưa xóa implementation cũ trong task này.

**Produces:** `AcquireScope()`, `InitializeAsync()`, `SettleScopeAsync()`, `RunGuardedAsync<T>()` và public scope contract.

- [ ] **Step 1: Viết test fail cho lease riêng và rollback propagation**

Các `[Fact]` bắt buộc:

```text
AcquireScope_Returns_Distinct_Leases_Over_One_Root
Dispose_Incomplete_Inner_Scope_Requests_Rollback_Without_Disposing_Root
Scope_Cannot_Change_Outcome_After_Completion
Double_Complete_Throws_And_Finalizes_Exactly_Once
```

Test lõi:

```csharp
[Fact]
public async Task Dispose_Incomplete_Inner_Scope_Requests_Rollback_Without_Disposing_Root()
{
    var connection = new ControlledDbConnection(initiallyOpen: true);
    var root = CreateRoot(connection);
    var outer = root.AcquireScope();
    var inner = root.AcquireScope();
    await root.InitializeAsync();

    inner.Dispose();

    Assert.True(root.RollbackRequested);
    Assert.Equal(1, root.ActiveScopeCount);
    Assert.False(connection.IsDisposed);

    await outer.CompleteAsync();
    Assert.Equal(UnitOfWorkCompletionOutcome.RolledBack, root.CompletionOutcome);
    Assert.True(connection.IsDisposed);
}
```

- [ ] **Step 2: Chạy test để xác nhận fail**

```powershell
dotnet test tests/UnitOfWork.Tests/UnitOfWork.Tests.csproj --filter FullyQualifiedName~ScopeLifecycleTests
```

Expected: compile failure vì các type mới chưa tồn tại.

- [ ] **Step 3: Tạo state machine và exception**

```csharp
internal enum UnitOfWorkLifecycleState
{
    Initializing,
    Active,
    Finalizing,
    Disposed,
    Faulted
}

internal enum UnitOfWorkCompletionOutcome
{
    None,
    Committed,
    RolledBack,
    Faulted
}
```

`UnitOfWorkScope` có state nguyên tử `Active`, `Completed`, `RollbackRequested`, `Abandoned`; `CompleteAsync()` và `RollbackAsync()` chỉ được chuyển từ `Active`. `Dispose()` trên scope active phải settle là `Abandoned`; dispose sau settle không làm gì.

- [ ] **Step 4: Implement root ownership**

`RootUnitOfWork` giữ raw connection/transaction, repository cache, active scope count, rollback flag, lifecycle state, operation gate, callback `isCurrentRoot` và `onRootFinished`. Chỉ khi scope count giảm về 0 mới finalize.

Pseudo-signatures cố định:

```csharp
internal UnitOfWorkScope AcquireScope();
internal Task InitializeAsync();
internal Task SettleScopeAsync(UnitOfWorkScopeOutcome outcome);
internal Task<T> RunGuardedAsync<T>(Func<Task<T>> operation);
internal T GetRepository<T>() where T : class;
```

- [ ] **Step 5: Chạy test và commit**

```powershell
dotnet test tests/UnitOfWork.Tests/UnitOfWork.Tests.csproj --filter FullyQualifiedName~ScopeLifecycleTests
git add src/UnitOfWork.Core tests/UnitOfWork.Tests/ScopeLifecycleTests.cs
git commit -m "feat: add root and scope lifecycle model"
```

---

### Task 2: Tạo transaction-bound connection, command và transaction facade

**Files:** Tạo ba `TransactionBoundDb*.cs`, `TransactionInvariantTests.cs`; sửa `CounterRepository.cs` sau khi test đỏ.

**Consumes:** `RootUnitOfWork.RunGuardedAsync<T>()`, raw root transaction nội bộ.

- [ ] **Step 1: Viết test fail cho automatic enlistment**

```csharp
[Fact]
public async Task Repository_Command_Without_Manual_Transaction_Is_Rolled_Back()
{
    using var db = new SqliteTestDb();
    var manager = CreateManager(db);

    using var scope = await manager.BeginAsync();
    scope.GetRepository<ICounterRepository>().Insert(7);
    await scope.RollbackAsync();

    Assert.Equal(0, db.CountRows());
}
```

Xóa `cmd.Transaction = _transaction` khỏi fixture trước khi chạy test để chứng minh behavior không còn phụ thuộc repository nhớ enlist.

- [ ] **Step 2: Viết test fail cho ownership boundary**

Các `[Fact]` bắt buộc:

```text
Connection_Facade_Rejects_Resource_Ownership_Operations
Command_Rejects_Foreign_Connection_And_Transaction
Transaction_Metadata_Cannot_Control_Root_Transaction
Raw_Connection_Is_Not_Exposed_Through_Public_Context
```

Mỗi thao tác sau phải ném `UnitOfWorkStateException`: `Open`, `Close`, `Dispose`, `BeginTransaction`, `ChangeDatabase`, set `ConnectionString`; command set transaction `null`/foreign; transaction `Commit`, `Rollback`, `Dispose`.

- [ ] **Step 3: Implement facades**

`TransactionBoundDbConnection.CreateCommand()` phải:

```csharp
public IDbCommand CreateCommand()
{
    _owner.EnsureUsable();
    var command = _inner.CreateCommand();
    command.Transaction = _transaction;
    return new TransactionBoundDbCommand(command, this, _transactionFacade, _owner);
}
```

`TransactionBoundDbCommand.Transaction` getter trả facade; setter chỉ chấp nhận cùng instance. `Connection` getter trả bound connection; setter chỉ chấp nhận cùng instance. Các `Execute*` tiếp tục đi qua root operation guard.

- [ ] **Step 4: Đổi repository factory và fixture**

Factory cuối cùng nhận `(Type type, IDbConnection connection)`. `CounterRepository` chỉ giữ connection:

```csharp
public CounterRepository(IDbConnection connection)
{
    _connection = connection;
}
```

- [ ] **Step 5: Chạy test và commit**

```powershell
dotnet test tests/UnitOfWork.Tests/UnitOfWork.Tests.csproj --filter FullyQualifiedName~TransactionInvariantTests
dotnet test tests/UnitOfWork.Tests/UnitOfWork.Tests.csproj --filter FullyQualifiedName~CommitRollbackIntegrationTests
git add src/UnitOfWork.Core tests/UnitOfWork.Tests
git commit -m "fix: bind every command to the root transaction"
```

---

### Task 3: Chuyển manager sang scope API và ambient theo instance

**Files:** Sửa `IUnitOfWorkManager.cs`, `UnitOfWorkManager.cs`; tạo `ManagerIsolationTests.cs`; sửa ambient tests.

**Produces:** `Task<IUnitOfWorkScope> BeginAsync()`, `IUnitOfWorkContext Current`, không còn public `ClearCurrent()`.

- [ ] **Step 1: Viết test fail cho manager isolation**

Các `[Fact]` bắt buộc:

```text
Every_Begin_Returns_A_Distinct_Scope_Over_One_Current_Context
Nested_Begin_During_Initialization_Returns_Distinct_Scopes_And_Awaits_One_Root
Reentrant_Begin_During_OpenAsync_Awaits_The_Same_Initialization
Two_Manager_Instances_Do_Not_Share_Ambient_Root
Outermost_Finalization_Clears_Current_Automatically
Initialization_Failure_Clears_Current_And_Allows_Fresh_Begin
```

- [ ] **Step 2: Thay static ambient bằng instance ambient**

```csharp
private readonly AsyncLocal<AmbientUnitOfWorkHolder?> _current = new();
```

Holder giữ `RootUnitOfWork? Root` và `Task? Initialization`. Callback cleanup chỉ clear khi holder vẫn tham chiếu đúng root đã hoàn tất.

- [ ] **Step 3: Giữ đúng initialization race behavior**

Phải publish holder và shared `TaskCompletionSource` trước khi bắt đầu async initialization:

```csharp
var root = CreateRoot(holder);
var completion = new TaskCompletionSource(
    TaskCreationOptions.RunContinuationsAsynchronously);
holder.Root = root;
holder.Initialization = completion.Task;
_current.Value = holder;

_ = InitializeRootAsync(root, holder, completion);
return AwaitScopeAsync(root.AcquireScope(), completion.Task);
```

Nested/reentrant begin luôn acquire scope mới nhưng cùng await một initialization task.

- [ ] **Step 4: Gỡ `ClearCurrent()` khỏi interface và manager**

Ambient chỉ được clear từ initialization-failure cleanup hoặc root finalization callback. Không cung cấp API cho caller xóa holder khi transaction còn active.

- [ ] **Step 5: Chạy test và commit**

```powershell
dotnet test tests/UnitOfWork.Tests/UnitOfWork.Tests.csproj --filter "FullyQualifiedName~ManagerIsolationTests|FullyQualifiedName~UnitOfWorkManagerAmbientTests"
git add src/UnitOfWork.Core tests/UnitOfWork.Tests
git commit -m "refactor: isolate ambient roots per manager"
```

---

### Task 4: Khóa finalization failure và operation/lifecycle concurrency

**Files:** Sửa `RootUnitOfWork.cs`, `UnitOfWorkScope.cs`, `ControlledDbConnection.cs`, scope/ambient/concurrency tests.

- [ ] **Step 1: Mở rộng controlled transaction fixture**

Thêm `CommitCount`, `RollbackCount`, `DisposeCount`, `commitException`, `rollbackException`; connection expose `LastTransaction`. Counter tăng trước khi ném exception để phát hiện retry.

- [ ] **Step 2: Viết failure tests**

Các `[Fact]` bắt buộc:

```text
Commit_Failure_Faults_Root_And_Is_Not_Retried
Rollback_Failure_Faults_Root_And_Is_Not_Retried
Cleanup_Attempts_Connection_When_Transaction_Disposal_Fails
Lifecycle_Finalization_While_Operation_Is_Active_Is_Rejected_Without_Settling_Scope
Commit_Failure_Clears_Manager_Current_And_Next_Begin_Is_Fresh
```

Test commit failure phải assert `CommitCount == 1`, root `Faulted`, connection disposed và lần gọi settle tiếp theo ném state exception mà không tăng counter.

- [ ] **Step 3: Implement deterministic terminal behavior**

- Chỉ revert scope về `Active` khi operation gate từ chối trước khi root state bị mutate.
- Commit/rollback/cleanup failure để scope settled và root non-retriable.
- Root chỉ chuyển `Active -> Finalizing` một lần.
- Cleanup luôn thử dispose cả transaction và connection.
- Callback clear ambient luôn chạy trong `finally`.
- Nếu operation đang active, finalization fail-fast bằng `UnitOfWorkConcurrencyException`; active scope count không bị giảm để caller có thể retry sau khi operation kết thúc.

- [ ] **Step 4: Chạy test và commit**

```powershell
dotnet test tests/UnitOfWork.Tests/UnitOfWork.Tests.csproj --filter "FullyQualifiedName~ScopeLifecycleTests|FullyQualifiedName~ConcurrencyGuardTests|FullyQualifiedName~DisposalTests"
dotnet test UnitOfWork.slnx --no-restore
git add src/UnitOfWork.Core tests/UnitOfWork.Tests
git commit -m "fix: make unit of work finalization deterministic"
```

---

### Task 5: Xóa legacy/static ambient và migrate toàn bộ test

**Files:** Sửa tất cả test còn lại; xóa các file legacy và test reset files trong File map.

- [ ] **Step 1: Rewrite flow-isolation test qua public manager/scope API**

Inherited execution context được dùng cùng scope tuần tự. Suppressed execution context giữ retained scope phải nhận `UnitOfWorkConcurrencyException`; message nói rõ current root của manager bị thiếu/foreign, không nhắc static `AmbientFlowId`.

- [ ] **Step 2: Làm concurrency tests deterministic**

Thay delay-only overlap bằng `TaskCompletionSource` barriers: operation đầu báo đã sở hữu gate; operation hai fail-fast; test release operation đầu; sau đó scope settle bình thường. Giữ ít nhất một test đi qua command wrapper thực tế.

- [ ] **Step 3: Rewrite disposal tests**

Bắt buộc có các case:

```text
Completed_Scope_Dispose_Is_Idempotent
Rollback_Completed_Scope_Dispose_Is_Idempotent
Incomplete_Outermost_Scope_Dispose_Rolls_Back_And_Clears_Current
Incomplete_Inner_Scope_Dispose_Does_Not_Dispose_Root
Fresh_Begin_Works_After_Root_Finalization_Without_ClearCurrent
```

- [ ] **Step 4: Xóa static reset và bật xUnit parallelization mặc định**

Xóa `UnitOfWorkTestBase.cs`, bỏ inheritance khỏi tất cả test class, xóa `AssemblyInfo.cs`. Không tạo global reset hook thay thế.

- [ ] **Step 5: Xóa implementation cũ**

Xóa `IUnitOfWork.cs`, `UnitOfWork.cs`, `GuardedDbConnection.cs`, `GuardedDbCommand.cs`. Xóa constructor repository nhận transaction và mọi manual transaction assignment.

- [ ] **Step 6: Scan forbidden symbols**

Không được còn match trong `src`/`tests`:

```text
whole-word IUnitOfWork
ClearCurrent(
AmbientFlowId
OwnerFlowId
IncrementRef(
GuardedDbConnection
GuardedDbCommand
ResetAmbientStateForTests
Func<Type, IDbConnection, IDbTransaction
```

`IUnitOfWorkScope` và `IUnitOfWorkContext` được phép.

- [ ] **Step 7: Chạy test lặp và commit**

```powershell
dotnet build UnitOfWork.slnx --no-restore --warnaserror
1..3 | ForEach-Object {
    dotnet test UnitOfWork.slnx --no-build --logger "console;verbosity=minimal"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
git add -A
git commit -m "refactor: remove legacy shared unit of work owner"
```

Expected: cả ba lần pass khi xUnit chạy song song mặc định; không stale ambient hoặc SQLite file-lock leakage.

---

### Task 6: Cập nhật README và thêm local verification script

**Files:** Sửa `README.md`; tạo `scripts/verify-p0-transaction-invariants.ps1`.

- [ ] **Step 1: Document contract cuối**

README phải nêu rõ distinct scope per begin, shared root transaction/cache, `CompleteAsync()` chỉ đánh dấu scope thành công, incomplete dispose ép rollback, inner dispose không sở hữu root resource, `manager.Current` là context view, automatic cleanup, transaction-bound connection, automatic command enlistment và manager isolation.

Migration table:

| Before | After |
|---|---|
| `IUnitOfWork` | `IUnitOfWorkScope` / `IUnitOfWorkContext` |
| `CommitAsync()` | `CompleteAsync()` |
| dispose + `ClearCurrent()` | `using var scope = await BeginAsync()` |
| factory `(type, connection, transaction)` | `(type, connection)` |
| `command.Transaction = transaction` | xóa assignment |
| nested begin trả cùng owner | nested begin trả distinct lease |

- [ ] **Step 2: Tạo verification script**

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

    dotnet test UnitOfWork.slnx --no-build --logger "console;verbosity=normal"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $files = Get-ChildItem src, tests -Recurse -File -Include *.cs, *.csproj
    $patterns = @(
        '\bIUnitOfWork\b',
        '\bClearCurrent\s*\(',
        '\bAmbientFlowId\b',
        '\bOwnerFlowId\b',
        '\bIncrementRef\s*\(',
        '\bGuardedDbConnection\b',
        '\bGuardedDbCommand\b',
        '\bResetAmbientStateForTests\b',
        'Func<Type,\s*IDbConnection,\s*IDbTransaction'
    )

    foreach ($pattern in $patterns) {
        $matches = $files | Select-String -Pattern $pattern
        if ($matches) {
            $matches | ForEach-Object { Write-Host $_.ToString() }
            throw "Forbidden legacy pattern remains: $pattern"
        }
    }

    Write-Host 'P0 transaction invariants verification passed.'
}
finally {
    Pop-Location
}
```

- [ ] **Step 3: Run verification**

```powershell
powershell -ExecutionPolicy Bypass -File scripts/verify-p0-transaction-invariants.ps1
```

Expected final line:

```text
P0 transaction invariants verification passed.
```

- [ ] **Step 4: Commit**

```bash
git add README.md scripts/verify-p0-transaction-invariants.ps1
git commit -m "docs: document p0 transaction invariant contract"
```

---

## Final Verification Gate

- [ ] Chạy `scripts/verify-p0-transaction-invariants.ps1` từ repository root.
- [ ] Chạy full test suite ba lần liên tiếp với xUnit parallelization mặc định.
- [ ] Xác nhận `git status --short` sạch.
- [ ] Xác nhận public API không có raw transaction property, `IUnitOfWork`, `ClearCurrent()`; `BeginAsync()` trả `Task<IUnitOfWorkScope>`.
- [ ] Xác nhận chỉ `RootUnitOfWork` giữ raw connection/transaction fields.
- [ ] Xác nhận cả sáu P0 roadmap items có executable test evidence.

Các `[Fact]` tối thiểu phải tồn tại và chạy:

```text
Repository_Command_Without_Manual_Transaction_Is_Rolled_Back
Connection_Facade_Rejects_Resource_Ownership_Operations
Command_Rejects_Foreign_Connection_And_Transaction
Transaction_Metadata_Cannot_Control_Root_Transaction
Every_Begin_Returns_A_Distinct_Scope_Over_One_Current_Context
Dispose_Incomplete_Inner_Scope_Requests_Rollback_Without_Disposing_Root
Double_Complete_Throws_And_Finalizes_Exactly_Once
Commit_Failure_Faults_Root_And_Is_Not_Retried
Lifecycle_Finalization_While_Operation_Is_Active_Is_Rejected_Without_Settling_Scope
Two_Manager_Instances_Do_Not_Share_Ambient_Root
```

## Out of Scope Follow-up

Tạo plan P1 riêng cho true async ADO.NET và cancellation, `IAsyncDisposable`, reader-lifetime operation lease, concurrent repository-cache policy, transaction options/isolation level và provider compatibility tests.
