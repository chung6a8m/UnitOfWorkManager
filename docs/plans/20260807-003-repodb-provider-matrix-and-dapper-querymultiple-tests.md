# Bổ sung test tương thích RepoDb provider matrix và Dapper QueryMultiple — Implementation Plan

> **Dành cho agent triển khai:** BẮT BUỘC dùng `superpowers:subagent-driven-development` (khuyến nghị) hoặc `superpowers:executing-plans` để thực hiện lần lượt từng task. Dùng checkbox `- [ ]` để theo dõi tiến độ.

**Goal:** Bổ sung test contract và integration test để chứng minh concurrency guard hiện tại tương thích với Dapper `QueryMultiple`, bắt regression RepoDb SQLite cold metadata discovery, và xác nhận RepoDb hoạt động đúng với SQL Server, PostgreSQL, MySQL (`MySql.Data`) và MySqlConnector mà không nới lỏng invariant một operation đang hoạt động trên mỗi root Unit of Work.

**Architecture:** Test được chia thành ba tầng. Tầng local luôn chạy dùng SQLite để kiểm tra Dapper `GridReader` lifetime và RepoDb SQLite metadata cache. Tầng provider matrix tách mỗi provider RepoDb thành một test project/process riêng vì `DbSettingMapper`, `DbHelperMapper`, `StatementBuilderMapper` và `DbFieldCache` là static trong process, trong khi mọi provider đều nhìn thấy cùng runtime type `TransactionBoundDbConnection`. Tầng orchestration dùng Docker Compose và PowerShell để khởi động database, truyền connection string bằng environment variable, chạy toàn bộ provider contract và fail nếu có provider test bị skip.

**Tech Stack:** .NET 8, C# latest, xUnit 2.9.2, Microsoft.NET.Test.Sdk 17.11.1, xunit.runner.visualstudio 2.8.2, coverlet.collector 6.0.2, Dapper 2.1.79, RepoDb 1.15.1, RepoDb provider extensions 1.15.0, Microsoft.Data.Sqlite 8.0.10, SQL Server 2022, PostgreSQL 16, MySQL 8.4, Docker Compose, PowerShell.

## Global Constraints

- Không thay đổi hoặc nới lỏng `RootUnitOfWork.EnterOperation`, `TransactionBoundDbCommand`, `TransactionBoundDbDataReader` hay `UnitOfWorkConcurrencyException` trong plan này.
- Nếu test phát hiện production bug mới, giữ test RED, ghi reproduction và tạo fix plan riêng. Không sửa production code trong cùng work package test compatibility.
- `Dapper.QueryMultiple*` là một operation kéo dài từ `ExecuteReader*` cho tới khi `GridReader` dispose reader cuối cùng.
- `Read(buffered: true)` chỉ buffer result set hiện tại; nó không giải phóng lease nếu còn result set tiếp theo.
- Mọi `GridReader`, `DbDataReader`, `DbCommand`, `DbConnection` và `IUnitOfWorkScope` phải được dispose rõ ràng kể cả khi test mong đợi exception.
- Test overlap phải giữ reader/GridReader sống thật; không dùng delay để đoán timing.
- Test cancellation phải dùng `TaskCompletionSource` để xác nhận reader đã mở trước khi cancel. `Task.Delay(Timeout.InfiniteTimeSpan, token)` chỉ được dùng làm cancellation barrier, không dùng làm timing heuristic.
- RepoDb cold-cache test phải gọi `DbFieldCache.Flush()` ngay trước operation kích hoạt discovery.
- RepoDb hot-cache test phải assert helper invocation count, không chỉ assert operation thứ hai thành công.
- RepoDb mapping phải đăng ký đủ `DbSettingMapper`, `DbHelperMapper` và `StatementBuilderMapper` cho `TransactionBoundDbConnection`.
- Không truyền raw provider transaction vào RepoDb/Dapper operation; transaction vẫn do `UnitOfWork.Core` sở hữu.
- Không chạy hai provider RepoDb trong cùng test process. Mỗi provider dùng một `.csproj` riêng.
- Mỗi RepoDb test assembly phải tắt xUnit parallelization vì mapper/cache là process-global.
- `dotnet test UnitOfWork.slnx` không được yêu cầu Docker. Provider facts phải skip rõ ràng nếu environment variable chưa có.
- `scripts/verify-repodb-dapper-compatibility.ps1` phải cấu hình đủ environment variables và fail nếu TRX có test skipped/notExecuted.
- Không thêm Testcontainers dependency; database lifecycle thuộc Docker Compose + PowerShell.
- Schema dùng table/column lowercase `counter`, `counter_archive`, `id`, `value` để tránh khác biệt case-sensitive.
- Mỗi provider contract test tự reset schema và không phụ thuộc thứ tự chạy.
- Test-only password dùng thống nhất `UowTest!2026`; không tái sử dụng ngoài local test containers.
- File Markdown/C#/PowerShell dùng UTF-8 và line ending theo policy repo.

---

## Contract matrix

### Dapper `QueryMultiple`

| ID | Contract | Kết quả mong đợi |
|---|---|---|
| DQM-01 | Sync `QueryMultiple` đọc 3 result sets. | Thành công trên một command/reader. |
| DQM-02 | Async `QueryMultipleAsync` + `ReadSingleAsync` đọc 3 result sets. | Thành công và release lease sau dispose. |
| DQM-03 | Chạy command khác sau grid 1 khi `GridReader` còn sống. | `UnitOfWorkConcurrencyException`. |
| DQM-04 | Dispose `GridReader`, sau đó chạy command khác. | Command sau thành công trên cùng scope. |
| DQM-05 | `Read(buffered: false)` break sớm rồi dispose. | Lease release; command sau thành công. |
| DQM-06 | Không đọc hết các grid nhưng dispose. | Lease release; command sau thành công. |
| DQM-07 | `ReadUnbufferedAsync` bị cancel trong lúc `ReadAsync` đang chờ. | Cancellation đi ra; command sau dispose thành công. |
| DQM-08 | Mapping callback chạy nested command. | `UnitOfWorkConcurrencyException`; reader vẫn cleanup. |

### RepoDb SQLite

| ID | Contract | Kết quả mong đợi |
|---|---|---|
| RSQL-01 | Cold-cache insert đầu tiên. | Metadata discovery không overlap command. |
| RSQL-02 | Hot-cache insert thứ hai cùng table. | Official helper được gọi đúng 1 lần. |
| RSQL-03 | Hai table trong cùng root. | Mỗi table discovery đúng 1 lần, tuần tự. |
| RSQL-04 | Insert trả identity. | Identity dương; row persist sau commit. |
| RSQL-05 | Discovery callback bị cancel khi reader đang giữ lease. | Reader dispose; command sau thành công. |
| RSQL-06 | Discovery callback throw sau khi reader đã mở. | Reader dispose; command sau thành công. |

### RepoDb provider matrix

Mỗi provider SQL Server, PostgreSQL, MySql.Data và MySqlConnector chạy đủ:

| ID | Contract | Kết quả mong đợi |
|---|---|---|
| RPM-01 | Cold-cache insert đầu tiên với official helper. | Thành công. |
| RPM-02 | Hot-cache insert thứ hai. | Helper count cho `counter` vẫn là 1. |
| RPM-03 | Discover `counter` và `counter_archive`. | Cả hai thành công, mỗi table 1 lần. |
| RPM-04 | Identity insert. | Identity dương và row persist. |
| RPM-05 | Cancel injected discovery callback khi reader đang sống. | Gate release; scalar sau đó thành công. |
| RPM-06 | Inject failure sau khi reader đã mở. | Gate release; scalar sau đó thành công. |
| RPM-07 | RepoDb insert trong lúc reader khác còn sống cùng root. | `UnitOfWorkConcurrencyException`; retry sau dispose thành công. |
| RPM-08 | Hai root độc lập insert đồng thời. | Cả hai commit thành công. |

---

## File map

**Create**

```text
tests/UnitOfWork.Tests/DapperQueryMultipleTests.cs
tests/UnitOfWork.Tests/Fixtures/ScriptedMultiResultDbConnection.cs
tests/UnitOfWork.Tests/Fixtures/ScriptedMultiResultDbCommand.cs
tests/UnitOfWork.Tests/Fixtures/ScriptedMultiResultDbDataReader.cs
tests/UnitOfWork.Tests/Fixtures/ScriptedDbTransaction.cs

tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests/AssemblyInfo.cs
tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests/RepoDbSqliteMetadataTests.cs
tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests/Fixtures/RepoDbSqliteTestDatabase.cs
tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests/Fixtures/CountingDbHelper.cs
tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests/Fixtures/InjectedDbHelper.cs

tests/UnitOfWork.RepoDb.ProviderTests.Shared/UnitOfWork.RepoDb.ProviderTests.Shared.csproj
tests/UnitOfWork.RepoDb.ProviderTests.Shared/IRepoDbProviderHarness.cs
tests/UnitOfWork.RepoDb.ProviderTests.Shared/RepoDbProviderContract.cs
tests/UnitOfWork.RepoDb.ProviderTests.Shared/CountingDbHelper.cs
tests/UnitOfWork.RepoDb.ProviderTests.Shared/InjectedDbHelper.cs
tests/UnitOfWork.RepoDb.ProviderTests.Shared/ExternalDatabaseFactAttribute.cs

tests/UnitOfWork.RepoDb.SqlServer.Tests/UnitOfWork.RepoDb.SqlServer.Tests.csproj
tests/UnitOfWork.RepoDb.SqlServer.Tests/AssemblyInfo.cs
tests/UnitOfWork.RepoDb.SqlServer.Tests/SqlServerProviderHarness.cs
tests/UnitOfWork.RepoDb.SqlServer.Tests/ProviderContractTests.cs

tests/UnitOfWork.RepoDb.PostgreSql.Tests/UnitOfWork.RepoDb.PostgreSql.Tests.csproj
tests/UnitOfWork.RepoDb.PostgreSql.Tests/AssemblyInfo.cs
tests/UnitOfWork.RepoDb.PostgreSql.Tests/PostgreSqlProviderHarness.cs
tests/UnitOfWork.RepoDb.PostgreSql.Tests/ProviderContractTests.cs

tests/UnitOfWork.RepoDb.MySql.Tests/UnitOfWork.RepoDb.MySql.Tests.csproj
tests/UnitOfWork.RepoDb.MySql.Tests/AssemblyInfo.cs
tests/UnitOfWork.RepoDb.MySql.Tests/MySqlProviderHarness.cs
tests/UnitOfWork.RepoDb.MySql.Tests/ProviderContractTests.cs

tests/UnitOfWork.RepoDb.MySqlConnector.Tests/UnitOfWork.RepoDb.MySqlConnector.Tests.csproj
tests/UnitOfWork.RepoDb.MySqlConnector.Tests/AssemblyInfo.cs
tests/UnitOfWork.RepoDb.MySqlConnector.Tests/MySqlConnectorProviderHarness.cs
tests/UnitOfWork.RepoDb.MySqlConnector.Tests/ProviderContractTests.cs

tests/containers/repodb-provider-matrix.compose.yml
scripts/verify-repodb-dapper-compatibility.ps1
```

**Modify**

```text
tests/UnitOfWork.Tests/UnitOfWork.Tests.csproj
tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests.csproj
UnitOfWork.slnx
README.md
```

---

### Task 1: Thêm Dapper QueryMultiple local contract tests

**Files:**
- Modify: `tests/UnitOfWork.Tests/UnitOfWork.Tests.csproj`
- Create: `tests/UnitOfWork.Tests/DapperQueryMultipleTests.cs`

**Interfaces:**
- Consumes: `SqliteTestDb`, `UnitOfWorkManager`, Dapper extension methods.
- Produces: DQM-01 đến DQM-06 và DQM-08.

- [ ] **Step 1: Thêm package**

```xml
<PackageReference Include="Dapper" Version="2.1.79" />
```

- [ ] **Step 2: Tạo manager helper**

```csharp
private static UnitOfWorkManager CreateManager(IDbConnectionFactory database) =>
    new(database, static (_, _) => new object());
```

- [ ] **Step 3: Viết sync happy-path**

```csharp
[Fact]
public async Task QueryMultiple_Reads_Three_Grids_Under_One_Lease()
{
    using var database = new SqliteTestDb();
    var manager = CreateManager(database);
    await using var scope = await manager.BeginAsync();

    using (var grid = scope.Connection.QueryMultiple(
        "SELECT 11; SELECT 22; SELECT 33;"))
    {
        Assert.Equal(11, grid.ReadSingle<int>());
        Assert.Equal(22, grid.ReadSingle<int>());
        Assert.Equal(33, grid.ReadSingle<int>());
    }

    Assert.Equal(1, await scope.Connection.ExecuteScalarAsync<int>("SELECT 1;"));
    await scope.RollbackAsync();
}
```

- [ ] **Step 4: Viết async happy-path**

Dùng `QueryMultipleAsync(new CommandDefinition("SELECT 11; SELECT 22; SELECT 33;"))`, đọc bằng `ReadSingleAsync<int>()`, dispose bằng `await using`, rồi assert `SELECT 1` thành công.

- [ ] **Step 5: Viết overlap rejection**

Giữ `GridReader` sống sau khi đọc grid 1, gọi `ExecuteScalarAsync<int>("SELECT 1;")`, assert exact `UnitOfWorkConcurrencyException`, sau đó đọc grid 2 và dispose.

- [ ] **Step 6: Viết ba recovery tests**

Tạo ba facts riêng:

1. Dispose grid sau grid đầu, command sau thành công.
2. `Read<int>(buffered: false)` lấy một row rồi break, dispose grid, command sau thành công.
3. Không đọc grid còn lại, dispose grid, command sau thành công.

- [ ] **Step 7: Viết nested callback test**

Dùng Dapper multi-mapping trên một result set; trong callback gọi `scope.Connection.ExecuteScalar<int>("SELECT 1;")` và assert `UnitOfWorkConcurrencyException`. `GridReader` phải nằm trong `using` để cleanup sau exception.

- [ ] **Step 8: Verify và commit**

```powershell
dotnet test tests/UnitOfWork.Tests/UnitOfWork.Tests.csproj --filter FullyQualifiedName~DapperQueryMultipleTests
git add tests/UnitOfWork.Tests/UnitOfWork.Tests.csproj tests/UnitOfWork.Tests/DapperQueryMultipleTests.cs
git commit -m "test: cover Dapper QueryMultiple reader lifetime"
```

Expected: DQM-01 đến DQM-06 và DQM-08 pass.

### Task 2: Thêm deterministic fixture cho Dapper async cancellation

**Files:**
- Create: `tests/UnitOfWork.Tests/Fixtures/ScriptedMultiResultDbConnection.cs`
- Create: `tests/UnitOfWork.Tests/Fixtures/ScriptedMultiResultDbCommand.cs`
- Create: `tests/UnitOfWork.Tests/Fixtures/ScriptedMultiResultDbDataReader.cs`
- Create: `tests/UnitOfWork.Tests/Fixtures/ScriptedDbTransaction.cs`
- Modify: `tests/UnitOfWork.Tests/DapperQueryMultipleTests.cs`

**Interfaces:**
- Produces: `ScriptedMultiResultDbConnection : DbConnection, IDbConnectionFactory`.
- Produces: `Task ReadStarted` và cancellation-aware `ReadAsync`.

- [ ] **Step 1: Implement connection/transaction tối thiểu**

`CreateConnection()` trả một scripted connection mới ở trạng thái open. `BeginDbTransactionAsync` trả `ScriptedDbTransaction`; commit/rollback chỉ ghi nhận trạng thái cho test.

- [ ] **Step 2: Implement command/reader state machine**

Command nhận:

```csharp
IReadOnlyList<IReadOnlyList<object?[]>> resultSets
```

Reader implement tối thiểu `Read`, `ReadAsync`, `NextResult`, `NextResultAsync`, `GetValue`, `GetName`, `GetFieldType`, `FieldCount`, `IsDBNull`. Khi `BlockNextRead` bật, `ReadAsync` set `ReadStarted` rồi chờ `TaskCompletionSource.Task.WaitAsync(token)`.

- [ ] **Step 3: Viết DQM-07**

```csharp
[Fact]
public async Task ReadUnbufferedAsync_Cancellation_Does_Not_Leak_Lease()
{
    var database = ScriptedMultiResultDbConnection.CreateBlockingReaderFixture();
    var manager = CreateManager(database);
    await using var scope = await manager.BeginAsync();

    using var cts = new CancellationTokenSource();
    await using (var grid = await scope.Connection.QueryMultipleAsync(
        new CommandDefinition("script", cancellationToken: cts.Token)))
    {
        var consumeTask = ConsumeAsync(grid.ReadUnbufferedAsync<int>());
        await database.ReadStarted;
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumeTask);
    }

    Assert.Equal(1, await scope.Connection.ExecuteScalarAsync<int>("scalar-one"));
    await scope.RollbackAsync();
}
```

`ConsumeAsync` phải enumerate thật bằng `await foreach`.

- [ ] **Step 4: Verify và commit**

```powershell
dotnet test tests/UnitOfWork.Tests/UnitOfWork.Tests.csproj --filter FullyQualifiedName~DapperQueryMultipleTests
git add tests/UnitOfWork.Tests
git commit -m "test: verify QueryMultiple async cancellation cleanup"
```

Expected: DQM-01 đến DQM-08 pass, không deadlock.

### Task 3: Bổ sung RepoDb SQLite metadata regression tests

**Files:**
- Modify: `tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests.csproj`
- Create: `tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests/AssemblyInfo.cs`
- Create: `tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests/RepoDbSqliteMetadataTests.cs`
- Create: `tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests/Fixtures/RepoDbSqliteTestDatabase.cs`
- Create: `tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests/Fixtures/CountingDbHelper.cs`
- Create: `tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests/Fixtures/InjectedDbHelper.cs`

**Interfaces:**
- Produces: RSQL-01 đến RSQL-06.
- Consumes: `TransactionBoundSqliteDbHelper` từ sample project.

- [ ] **Step 1: Thêm explicit packages và serial execution**

```xml
<PackageReference Include="RepoDb" Version="1.15.1" />
<PackageReference Include="RepoDb.Sqlite.Microsoft" Version="1.15.0" />
```

```csharp
using Xunit;
[assembly: CollectionBehavior(DisableTestParallelization = true)]
```

- [ ] **Step 2: Tạo SQLite fixture**

Tạo file SQLite riêng với `Pooling=False` và schema:

```sql
CREATE TABLE counter (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    value INTEGER NOT NULL
);
CREATE TABLE counter_archive (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    value INTEGER NOT NULL
);
```

Fixture implement `IDbConnectionFactory` và `CountAsync(string tableName)` bằng independent raw connection.

- [ ] **Step 3: Implement helper wrappers**

`CountingDbHelper` implement toàn bộ `IDbHelper`, delegate official helper, increment count theo table trong cả `GetFields` và `GetFieldsAsync`.

`InjectedDbHelper` implement toàn bộ `IDbHelper`, có callback:

```csharp
Func<IDbConnection, string, IDbTransaction?, CancellationToken, Task>? BeforeGetFieldsAsync
```

Mọi member khác delegate official helper.

- [ ] **Step 4: Register production-equivalent mappings**

```csharp
GlobalConfiguration.Setup().UseSqlite();
DbSettingMapper.Add<TransactionBoundDbConnection>(DbSettingMapper.Get<SqliteConnection>(), true);
StatementBuilderMapper.Add<TransactionBoundDbConnection>(StatementBuilderMapper.Get<SqliteConnection>(), true);
```

Mỗi test map `DbHelperMapper` với official/counting/injected helper cần dùng và gọi `DbFieldCache.Flush()` trước cold path.

- [ ] **Step 5: Viết RSQL-01 đến RSQL-04**

Cold/hot flow:

```text
Flush
→ root 1 insert counter(10), commit
→ root 2 insert counter(20) không flush, commit
→ helper count(counter) == 1
→ persisted count(counter) == 2
```

Two-table test insert tuần tự vào `counter` và `counter_archive` trong cùng root, assert mỗi table helper count bằng 1.

Identity test:

```csharp
var identity = await scope.Connection.InsertAsync(
    "counter",
    new { value = 42 });
Assert.True(Convert.ToInt64(identity) > 0);
```

- [ ] **Step 6: Viết deterministic RSQL-05**

Injected callback phải mở và giữ reader thật:

```csharp
await using var command = ((DbConnection)connection).CreateCommand();
command.CommandText = "SELECT 1;";
await using var reader = await command.ExecuteReaderAsync(cancellationToken);
Assert.True(await reader.ReadAsync(cancellationToken));
operationStarted.TrySetResult();
await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
```

Test gọi trực tiếp `mappedHelper.GetFieldsAsync(scope.Connection, "counter", null, token)`, chờ `operationStarted`, cancel, assert cancellation, restore official helper và assert `SELECT 1` thành công trên cùng scope.

- [ ] **Step 7: Viết RSQL-06**

Injected callback mở reader `SELECT 1`, đọc một row, rồi throw `InvalidOperationException("Injected metadata failure")` trong `await using`. Sau exception, restore official helper và assert scalar thành công trên cùng scope.

- [ ] **Step 8: Verify và commit**

```powershell
dotnet test tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests.csproj --filter FullyQualifiedName~RepoDbSqliteMetadataTests
dotnet test tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests.csproj
git add tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests
git commit -m "test: cover RepoDb SQLite metadata discovery lifecycle"
```

Expected: RSQL-01 đến RSQL-06 và HTTP integration test hiện có đều pass.

### Task 4: Tạo shared RepoDb provider contract library

**Files:**
- Create: `tests/UnitOfWork.RepoDb.ProviderTests.Shared/UnitOfWork.RepoDb.ProviderTests.Shared.csproj`
- Create: `tests/UnitOfWork.RepoDb.ProviderTests.Shared/IRepoDbProviderHarness.cs`
- Create: `tests/UnitOfWork.RepoDb.ProviderTests.Shared/RepoDbProviderContract.cs`
- Create: `tests/UnitOfWork.RepoDb.ProviderTests.Shared/CountingDbHelper.cs`
- Create: `tests/UnitOfWork.RepoDb.ProviderTests.Shared/InjectedDbHelper.cs`
- Create: `tests/UnitOfWork.RepoDb.ProviderTests.Shared/ExternalDatabaseFactAttribute.cs`

**Interfaces:**
- Produces: provider-neutral methods RPM-01 đến RPM-08.

- [ ] **Step 1: Tạo shared project**

Target `net8.0`; reference `UnitOfWork.Core`, RepoDb 1.15.1 và xUnit 2.9.2. Không reference concrete provider package.

- [ ] **Step 2: Định nghĩa harness**

```csharp
public interface IRepoDbProviderHarness : IAsyncDisposable
{
    string ProviderName { get; }
    string ScalarOneSql { get; }

    IDbHelper GetOfficialHelper();
    void RegisterMappings(IDbHelper helper);
    IDbConnectionFactory CreateConnectionFactory();
    Task ResetSchemaAsync(CancellationToken cancellationToken = default);
    Task<long> CountRowsAsync(string tableName, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 3: Implement `ExternalDatabaseFactAttribute`**

```csharp
public sealed class ExternalDatabaseFactAttribute : FactAttribute
{
    public ExternalDatabaseFactAttribute(string environmentVariable)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(environmentVariable)))
            Skip = $"Set {environmentVariable} to run this provider integration test.";
    }
}
```

- [ ] **Step 4: Implement shared wrappers**

`CountingDbHelper` dùng `ConcurrentDictionary<string, int>` và delegate toàn bộ `IDbHelper` methods.

`InjectedDbHelper` nhận `BeforeGetFieldsAsync` callback có signature giống Task 3 và delegate các member còn lại.

- [ ] **Step 5: Implement RPM-01 đến RPM-04**

Dùng manager:

```csharp
private static UnitOfWorkManager CreateManager(IRepoDbProviderHarness harness) =>
    new(harness.CreateConnectionFactory(), static (_, _) => new object());
```

Mỗi contract gọi `ResetSchemaAsync`, đăng ký counting official helper, `DbFieldCache.Flush()`, rồi thực hiện cold/hot/two-table/identity flow giống bảng contract.

- [ ] **Step 6: Implement RPM-05 deterministic cancellation**

Injected callback mở reader bằng `harness.ScalarOneSql`, đọc một row, signal `started`, rồi chờ `Task.Delay(Timeout.InfiniteTimeSpan, token)`. Test cancel sau `started`, assert exception là cancellation và không phải `UnitOfWorkConcurrencyException`, restore official helper, rồi chạy scalar trên cùng scope.

- [ ] **Step 7: Implement RPM-06**

Injected callback mở reader bằng `ScalarOneSql`, đọc một row, throw injected exception trong `await using`, restore helper và verify scalar sau đó thành công.

- [ ] **Step 8: Implement RPM-07/RPM-08**

RPM-07 giữ ADO.NET reader trên `SELECT id, value FROM counter`, gọi RepoDb insert và assert `UnitOfWorkConcurrencyException`; dispose reader rồi retry thành công.

RPM-08 tạo hai `UnitOfWorkManager` độc lập, dùng `TaskCompletionSource` barrier để bắt đầu hai insert gần nhau, complete cả hai và assert row count tăng 2.

- [ ] **Step 9: Commit**

```powershell
git add tests/UnitOfWork.RepoDb.ProviderTests.Shared
git commit -m "test: add shared RepoDb provider compatibility contracts"
```

### Task 5: Thêm SQL Server provider project

**Files:**
- Create: `tests/UnitOfWork.RepoDb.SqlServer.Tests/UnitOfWork.RepoDb.SqlServer.Tests.csproj`
- Create: `tests/UnitOfWork.RepoDb.SqlServer.Tests/AssemblyInfo.cs`
- Create: `tests/UnitOfWork.RepoDb.SqlServer.Tests/SqlServerProviderHarness.cs`
- Create: `tests/UnitOfWork.RepoDb.SqlServer.Tests/ProviderContractTests.cs`

- [ ] **Step 1: Tạo project**

Reference Microsoft.NET.Test.Sdk 17.11.1, xUnit 2.9.2, runner 2.8.2, coverlet 6.0.2, RepoDb 1.15.1, RepoDb.SqlServer 1.15.0, shared project và `UnitOfWork.Core`. Tắt parallelization bằng assembly attribute.

- [ ] **Step 2: Implement schema và mappings**

```sql
IF OBJECT_ID('dbo.counter_archive', 'U') IS NOT NULL DROP TABLE dbo.counter_archive;
IF OBJECT_ID('dbo.counter', 'U') IS NOT NULL DROP TABLE dbo.counter;
CREATE TABLE dbo.counter (id BIGINT IDENTITY(1,1) PRIMARY KEY, value INT NOT NULL);
CREATE TABLE dbo.counter_archive (id BIGINT IDENTITY(1,1) PRIMARY KEY, value INT NOT NULL);
```

```csharp
GlobalConfiguration.Setup().UseSqlServer();
DbSettingMapper.Add<TransactionBoundDbConnection>(DbSettingMapper.Get<SqlConnection>(), true);
DbHelperMapper.Add<TransactionBoundDbConnection>(helper, true);
StatementBuilderMapper.Add<TransactionBoundDbConnection>(StatementBuilderMapper.Get<SqlConnection>(), true);
```

Harness đọc `UOW_TEST_SQLSERVER`; `ScalarOneSql = "SELECT 1;"`.

- [ ] **Step 3: Wire 8 facts và verify**

Mỗi fact dùng `[ExternalDatabaseFact("UOW_TEST_SQLSERVER")]` và gọi một RPM method.

```powershell
$env:UOW_TEST_SQLSERVER = "Server=127.0.0.1,14333;Database=uow_tests;User Id=sa;Password=UowTest!2026;Encrypt=False;TrustServerCertificate=True"
dotnet test tests/UnitOfWork.RepoDb.SqlServer.Tests/UnitOfWork.RepoDb.SqlServer.Tests.csproj
```

Expected: 8 passed, 0 failed, 0 skipped.

- [ ] **Step 4: Commit**

```powershell
git add tests/UnitOfWork.RepoDb.SqlServer.Tests
git commit -m "test: add RepoDb SQL Server compatibility matrix"
```

### Task 6: Thêm PostgreSQL provider project

**Files:**
- Create: `tests/UnitOfWork.RepoDb.PostgreSql.Tests/UnitOfWork.RepoDb.PostgreSql.Tests.csproj`
- Create: `tests/UnitOfWork.RepoDb.PostgreSql.Tests/AssemblyInfo.cs`
- Create: `tests/UnitOfWork.RepoDb.PostgreSql.Tests/PostgreSqlProviderHarness.cs`
- Create: `tests/UnitOfWork.RepoDb.PostgreSql.Tests/ProviderContractTests.cs`

- [ ] **Step 1: Tạo project**

Reference Microsoft.NET.Test.Sdk 17.11.1, xUnit 2.9.2, runner 2.8.2, coverlet 6.0.2, RepoDb 1.15.1, RepoDb.PostgreSql 1.15.0, shared project và `UnitOfWork.Core`. Tắt parallelization.

- [ ] **Step 2: Implement schema và mappings**

```sql
DROP TABLE IF EXISTS counter_archive;
DROP TABLE IF EXISTS counter;
CREATE TABLE counter (id BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY, value INTEGER NOT NULL);
CREATE TABLE counter_archive (id BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY, value INTEGER NOT NULL);
```

```csharp
GlobalConfiguration.Setup().UsePostgreSql();
DbSettingMapper.Add<TransactionBoundDbConnection>(DbSettingMapper.Get<NpgsqlConnection>(), true);
DbHelperMapper.Add<TransactionBoundDbConnection>(helper, true);
StatementBuilderMapper.Add<TransactionBoundDbConnection>(StatementBuilderMapper.Get<NpgsqlConnection>(), true);
```

Harness đọc `UOW_TEST_POSTGRESQL`; `ScalarOneSql = "SELECT 1;"`.

- [ ] **Step 3: Wire 8 facts và verify**

```powershell
$env:UOW_TEST_POSTGRESQL = "Host=127.0.0.1;Port=15432;Database=uow_tests;Username=postgres;Password=UowTest!2026"
dotnet test tests/UnitOfWork.RepoDb.PostgreSql.Tests/UnitOfWork.RepoDb.PostgreSql.Tests.csproj
```

Expected: 8 passed, 0 failed, 0 skipped.

- [ ] **Step 4: Commit**

```powershell
git add tests/UnitOfWork.RepoDb.PostgreSql.Tests
git commit -m "test: add RepoDb PostgreSQL compatibility matrix"
```

### Task 7: Thêm MySql.Data provider project

**Files:**
- Create: `tests/UnitOfWork.RepoDb.MySql.Tests/UnitOfWork.RepoDb.MySql.Tests.csproj`
- Create: `tests/UnitOfWork.RepoDb.MySql.Tests/AssemblyInfo.cs`
- Create: `tests/UnitOfWork.RepoDb.MySql.Tests/MySqlProviderHarness.cs`
- Create: `tests/UnitOfWork.RepoDb.MySql.Tests/ProviderContractTests.cs`

- [ ] **Step 1: Tạo project**

Reference Microsoft.NET.Test.Sdk 17.11.1, xUnit 2.9.2, runner 2.8.2, coverlet 6.0.2, RepoDb 1.15.1, RepoDb.MySql 1.15.0, shared project và `UnitOfWork.Core`. Tắt parallelization.

- [ ] **Step 2: Implement schema và mappings**

```sql
DROP TABLE IF EXISTS counter_archive;
DROP TABLE IF EXISTS counter;
CREATE TABLE counter (id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY, value INT NOT NULL) ENGINE=InnoDB;
CREATE TABLE counter_archive (id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY, value INT NOT NULL) ENGINE=InnoDB;
```

```csharp
GlobalConfiguration.Setup().UseMySql();
DbSettingMapper.Add<TransactionBoundDbConnection>(DbSettingMapper.Get<MySql.Data.MySqlClient.MySqlConnection>(), true);
DbHelperMapper.Add<TransactionBoundDbConnection>(helper, true);
StatementBuilderMapper.Add<TransactionBoundDbConnection>(StatementBuilderMapper.Get<MySql.Data.MySqlClient.MySqlConnection>(), true);
```

Harness đọc `UOW_TEST_MYSQL`; `ScalarOneSql = "SELECT 1;"`.

- [ ] **Step 3: Wire 8 facts và verify**

```powershell
$env:UOW_TEST_MYSQL = "Server=127.0.0.1;Port=13306;Database=uow_tests;User ID=root;Password=UowTest!2026;SslMode=None"
dotnet test tests/UnitOfWork.RepoDb.MySql.Tests/UnitOfWork.RepoDb.MySql.Tests.csproj
```

Expected: 8 passed, 0 failed, 0 skipped.

- [ ] **Step 4: Commit**

```powershell
git add tests/UnitOfWork.RepoDb.MySql.Tests
git commit -m "test: add RepoDb MySql.Data compatibility matrix"
```

### Task 8: Thêm MySqlConnector provider project

**Files:**
- Create: `tests/UnitOfWork.RepoDb.MySqlConnector.Tests/UnitOfWork.RepoDb.MySqlConnector.Tests.csproj`
- Create: `tests/UnitOfWork.RepoDb.MySqlConnector.Tests/AssemblyInfo.cs`
- Create: `tests/UnitOfWork.RepoDb.MySqlConnector.Tests/MySqlConnectorProviderHarness.cs`
- Create: `tests/UnitOfWork.RepoDb.MySqlConnector.Tests/ProviderContractTests.cs`

- [ ] **Step 1: Tạo project**

Reference Microsoft.NET.Test.Sdk 17.11.1, xUnit 2.9.2, runner 2.8.2, coverlet 6.0.2, RepoDb 1.15.1, RepoDb.MySqlConnector 1.15.0, shared project và `UnitOfWork.Core`. Tắt parallelization.

- [ ] **Step 2: Implement schema và mappings**

Dùng cùng schema InnoDB của Task 7, nhưng concrete connection là `MySqlConnector.MySqlConnection` và bootstrap là:

```csharp
GlobalConfiguration.Setup().UseMySqlConnector();
DbSettingMapper.Add<TransactionBoundDbConnection>(DbSettingMapper.Get<MySqlConnector.MySqlConnection>(), true);
DbHelperMapper.Add<TransactionBoundDbConnection>(helper, true);
StatementBuilderMapper.Add<TransactionBoundDbConnection>(StatementBuilderMapper.Get<MySqlConnector.MySqlConnection>(), true);
```

Harness đọc `UOW_TEST_MYSQL`; `ScalarOneSql = "SELECT 1;"`.

- [ ] **Step 3: Wire 8 facts và verify**

```powershell
$env:UOW_TEST_MYSQL = "Server=127.0.0.1;Port=13306;Database=uow_tests;User ID=root;Password=UowTest!2026;SslMode=None"
dotnet test tests/UnitOfWork.RepoDb.MySqlConnector.Tests/UnitOfWork.RepoDb.MySqlConnector.Tests.csproj
```

Expected: 8 passed, 0 failed, 0 skipped.

- [ ] **Step 4: Commit**

```powershell
git add tests/UnitOfWork.RepoDb.MySqlConnector.Tests
git commit -m "test: add RepoDb MySqlConnector compatibility matrix"
```

### Task 9: Thêm Docker Compose và verification script

**Files:**
- Create: `tests/containers/repodb-provider-matrix.compose.yml`
- Create: `scripts/verify-repodb-dapper-compatibility.ps1`

- [ ] **Step 1: Định nghĩa containers**

Compose services và ports:

```text
uow-sqlserver  -> 14333:1433, mcr.microsoft.com/mssql/server:2022-latest
uow-postgresql -> 15432:5432, postgres:16-alpine
uow-mysql      -> 13306:3306, mysql:8.4
```

Dùng password `UowTest!2026`; PostgreSQL/MySQL tạo sẵn database `uow_tests`. SQL Server database được script tạo sau health check.

- [ ] **Step 2: Thêm health checks và script parameters**

```powershell
param(
    [switch]$KeepContainers,
    [int]$SqlServerPort = 14333,
    [int]$PostgreSqlPort = 15432,
    [int]$MySqlPort = 13306
)
```

Script fail-fast nếu thiếu `docker`, `docker compose` hoặc `dotnet`; dùng `docker compose ... up -d --wait`; tạo SQL Server database `uow_tests` bằng `sqlcmd` trong container.

- [ ] **Step 3: Chạy local contracts**

```powershell
dotnet test tests/UnitOfWork.Tests/UnitOfWork.Tests.csproj --filter FullyQualifiedName~DapperQueryMultipleTests
dotnet test tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests.csproj --filter FullyQualifiedName~RepoDbSqliteMetadataTests
```

- [ ] **Step 4: Set environment variables và chạy provider projects**

Set `UOW_TEST_SQLSERVER`, `UOW_TEST_POSTGRESQL`, `UOW_TEST_MYSQL` bằng parameter ports và password cố định. Mỗi project xuất TRX riêng.

- [ ] **Step 5: Parse TRX**

Mỗi provider TRX phải có:

```text
passed = 8
failed = 0
skipped/notExecuted = 0
```

- [ ] **Step 6: Cleanup và commit**

Trong `finally`, nếu không có `-KeepContainers`, chạy `docker compose ... down -v`.

```powershell
git add tests/containers scripts/verify-repodb-dapper-compatibility.ps1
git commit -m "test: add provider compatibility verification environment"
```

### Task 10: Tích hợp solution, README và full verification

**Files:**
- Modify: `UnitOfWork.slnx`
- Modify: `README.md`

- [ ] **Step 1: Thêm projects vào solution**

Thêm shared project và bốn provider test projects dưới `/tests/`.

- [ ] **Step 2: Document test tiers**

README phải giải thích:

1. `dotnet test UnitOfWork.slnx` chạy local tests và skip external provider facts khi env vars không có.
2. `scripts/verify-repodb-dapper-compatibility.ps1` chạy full matrix và cần Docker.
3. RepoDb static mapping/cache là lý do tách provider theo process.
4. `GridReader` còn sống đồng nghĩa root operation lease vẫn bị giữ.

- [ ] **Step 3: Default verification**

```powershell
dotnet restore UnitOfWork.slnx
dotnet build UnitOfWork.slnx --no-restore --warnaserror
dotnet test UnitOfWork.slnx --no-build --logger "console;verbosity=normal"
```

Expected: build không warning/error; local tests pass; external provider facts chỉ skip khi env vars không có.

- [ ] **Step 4: Full provider verification**

```powershell
powershell -ExecutionPolicy Bypass -File scripts/verify-repodb-dapper-compatibility.ps1
```

Expected:

```text
Dapper QueryMultiple contracts: PASS
RepoDb SQLite metadata contracts: PASS
RepoDb SQL Server contracts: 8 PASS, 0 SKIP
RepoDb PostgreSQL contracts: 8 PASS, 0 SKIP
RepoDb MySql.Data contracts: 8 PASS, 0 SKIP
RepoDb MySqlConnector contracts: 8 PASS, 0 SKIP
```

- [ ] **Step 5: Existing regression scripts**

```powershell
powershell -ExecutionPolicy Bypass -File scripts/verify-p0-transaction-invariants.ps1
powershell -ExecutionPolicy Bypass -File scripts/verify-p1-async-concurrency-boundary.ps1
```

- [ ] **Step 6: Final commit**

```powershell
git add UnitOfWork.slnx README.md
git commit -m "docs: document RepoDb and Dapper compatibility tests"
```

---

## Definition of Done

- DQM-01 đến DQM-08 có test deterministic và pass.
- RSQL-01 đến RSQL-06 có direct integration test và pass.
- SQL Server, PostgreSQL, MySql.Data và MySqlConnector mỗi provider chạy RPM-01 đến RPM-08 trong process riêng.
- Cold-cache test gọi `DbFieldCache.Flush()`; hot-cache test assert helper invocation count.
- Cancellation/failure probe chứng minh command sau vẫn chạy được trên cùng scope.
- Same-root overlap bị `UnitOfWorkConcurrencyException`; independent roots chạy đồng thời thành công.
- Default solution test không cần Docker.
- Full compatibility script fail nếu provider test skip.
- Không có thay đổi production code hoặc nới lỏng concurrency guard trong implementation của plan này.
- README giải thích RepoDb metadata discovery và Dapper `GridReader` lifetime.
