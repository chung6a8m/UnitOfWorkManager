# Bổ sung test tương thích RepoDb provider matrix và Dapper QueryMultiple — Implementation Plan

> **Dành cho agent triển khai:** BẮT BUỘC dùng `superpowers:subagent-driven-development` (khuyến nghị) hoặc `superpowers:executing-plans` để thực hiện lần lượt từng task. Dùng checkbox `- [ ]` để theo dõi tiến độ.

**Goal:** Bổ sung test contract và integration test để chứng minh concurrency guard hiện tại tương thích với Dapper `QueryMultiple`, bắt regression RepoDb SQLite cold metadata discovery, và xác nhận RepoDb hoạt động đúng với SQL Server, PostgreSQL, MySQL (`MySql.Data`) và MySqlConnector mà không nới lỏng invariant một operation đang hoạt động trên mỗi root Unit of Work.

**Architecture:** Chia test thành ba lớp. Lớp local luôn chạy dùng SQLite để kiểm tra Dapper `GridReader` lifetime và RepoDb SQLite metadata cache. Lớp provider matrix tách mỗi provider RepoDb thành một test project/process riêng vì `DbSettingMapper`, `DbHelperMapper`, `StatementBuilderMapper` và `DbFieldCache` là static trong process, trong khi mọi provider đều nhìn thấy cùng runtime type `TransactionBoundDbConnection`. Lớp orchestration dùng Docker Compose và PowerShell để khởi động database, truyền connection string bằng environment variable, chạy toàn bộ provider contract, và bảo đảm không có test bị skip trong verification chuyên dụng.

**Tech Stack:** .NET 8, C# latest, xUnit 2.9.2, Dapper 2.1.79, RepoDb 1.15.1, RepoDb provider extensions 1.15.0, Microsoft.Data.Sqlite 8.0.10, SQL Server 2022 container, PostgreSQL 16 container, MySQL 8.4 container, Docker Compose, PowerShell.

## Global Constraints

- Không thay đổi hoặc nới lỏng `RootUnitOfWork.EnterOperation`, `TransactionBoundDbCommand`, `TransactionBoundDbDataReader` hay `UnitOfWorkConcurrencyException` trong plan này.
- Nếu test phát hiện production bug mới, dừng ở RED, ghi rõ reproduction, và tạo fix plan riêng. Không sửa production code lẫn vào commit test compatibility.
- `Dapper.QueryMultiple*` phải được xem là một operation kéo dài từ `ExecuteReader*` cho tới khi `GridReader` dispose reader cuối cùng.
- `Read(buffered: true)` chỉ buffer result set hiện tại; nó không giải phóng lease khi còn result set tiếp theo.
- Mọi test mở `GridReader`, `DbDataReader`, `DbCommand`, `DbConnection` hoặc `IUnitOfWorkScope` phải dispose rõ ràng bằng `using`/`await using`, kể cả test mong đợi exception.
- Test overlap phải dùng reader/GridReader còn sống để tạo overlap thật; không dùng `Task.Delay` để đoán timing.
- Test cancellation phải dùng `TaskCompletionSource`/barrier để biết operation đã bắt đầu trước khi cancel.
- RepoDb cold-cache test phải gọi `DbFieldCache.Flush()` ngay trước hành vi cần kích hoạt discovery.
- RepoDb hot-cache test phải chứng minh helper chỉ bị gọi đúng một lần, không chỉ kiểm tra operation thứ hai “không lỗi”.
- RepoDb provider mapping phải đăng ký đủ ba mapper cho `TransactionBoundDbConnection`: `DbSettingMapper`, `DbHelperMapper`, `StatementBuilderMapper`.
- Không dùng raw provider transaction trong repository/test operation. Transaction vẫn do `UnitOfWork.Core` sở hữu và command vẫn được auto-bind qua facade.
- Không chạy hai provider RepoDb trong cùng test process. Mỗi provider có một `.csproj` riêng.
- Mỗi provider test assembly phải tắt xUnit parallelization vì mapper/cache RepoDb là process-global.
- `dotnet test UnitOfWork.slnx` không được yêu cầu Docker. Provider tests phải skip có chủ đích khi environment variable tương ứng chưa được cấu hình.
- `scripts/verify-repodb-dapper-compatibility.ps1` phải khởi động containers, cấu hình environment variables, chạy từng provider project, và fail nếu có test bị skip.
- Không thêm Testcontainers dependency. Database lifecycle do Docker Compose + PowerShell quản lý để version container và port được nhìn thấy rõ.
- SQL/schema dùng tên lowercase `counter` và `counter_archive` để tránh khác biệt case-sensitive giữa PostgreSQL/Linux MySQL và SQL Server.
- Dữ liệu test phải được reset trước mỗi provider contract test; không phụ thuộc thứ tự test.
- File Markdown/C#/PowerShell dùng UTF-8 theo policy repo; checkout line ending tuân theo `.editorconfig`/`.gitattributes`.

---

## Compatibility contracts bắt buộc

### Dapper `QueryMultiple`

| ID | Contract | Kết quả mong đợi |
|---|---|---|
| DQM-01 | Sync `QueryMultiple` đọc 3 result sets trên một command/reader. | Thành công; không có `UnitOfWorkConcurrencyException`. |
| DQM-02 | Async `QueryMultipleAsync` + `ReadAsync` đọc 3 result sets. | Thành công; reader cuối cùng dispose và lease release. |
| DQM-03 | Chạy command khác sau khi đọc grid 1 nhưng trước khi dispose grid. | Bị từ chối bằng `UnitOfWorkConcurrencyException`. |
| DQM-04 | Dispose `GridReader`, sau đó chạy command khác. | Command sau thành công trên cùng scope. |
| DQM-05 | `Read(buffered: false)` break sớm rồi dispose grid. | Lease release; command sau thành công. |
| DQM-06 | Không đọc hết các grid nhưng dispose grid. | Lease release; command sau thành công. |
| DQM-07 | `ReadUnbufferedAsync` bị cancel trong lúc `ReadAsync` đang chờ. | Cancellation đi ra; grid dispose; command sau thành công. |
| DQM-08 | Mapping callback cố chạy nested command. | Bị `UnitOfWorkConcurrencyException`; grid vẫn dispose an toàn. |

### RepoDb SQLite

| ID | Contract | Kết quả mong đợi |
|---|---|---|
| RSQL-01 | Cold cache entity insert đầu tiên. | Schema discovery chạy tuần tự; không overlap command. |
| RSQL-02 | Hot cache operation thứ hai cùng table. | Helper invocation count vẫn là 1. |
| RSQL-03 | Hai table khác nhau trong cùng root. | Mỗi table discovery đúng một lần, tuần tự. |
| RSQL-04 | Insert có identity. | Trả identity dương và row persist sau commit. |
| RSQL-05 | Inject discovery cancellation khi command đang active. | Exception đi ra; lease không leak; command sau thành công. |
| RSQL-06 | Inject discovery failure sau khi reader đã mở. | Reader dispose; lease không leak; command sau thành công. |

### RepoDb provider matrix

Mỗi provider phải chạy cùng contract sau:

| ID | Contract | Kết quả mong đợi |
|---|---|---|
| RPM-01 | Cold metadata cache insert đầu tiên. | Thành công với helper chính thức của provider. |
| RPM-02 | Hot metadata cache insert thứ hai. | Helper gọi đúng một lần cho `counter`. |
| RPM-03 | Hai table độc lập. | `counter` và `counter_archive` đều discover thành công. |
| RPM-04 | Identity insert. | Identity dương; row tồn tại sau commit. |
| RPM-05 | Discovery cancellation probe. | Gate release; scalar sau cancellation thành công. |
| RPM-06 | Discovery failure probe khi reader đang sống. | Gate release; scalar sau failure thành công. |
| RPM-07 | RepoDb operation trong lúc reader khác còn sống cùng root. | `UnitOfWorkConcurrencyException`. |
| RPM-08 | Hai root độc lập chạy operation đồng thời. | Cả hai thành công; không dùng chung transaction/lease. |

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

### Task 1: Thêm dependency và test specification cho Dapper QueryMultiple

**Files:**
- Modify: `tests/UnitOfWork.Tests/UnitOfWork.Tests.csproj`
- Create: `tests/UnitOfWork.Tests/DapperQueryMultipleTests.cs`

**Interfaces:**
- Consumes: `SqliteTestDb`, `UnitOfWorkManager`, `TransactionBoundDbConnection`, Dapper 2.1.79.
- Produces: test class `DapperQueryMultipleTests` chứa contract DQM-01 đến DQM-06 và DQM-08.

- [ ] **Step 1: Thêm explicit Dapper package reference**

```xml
<PackageReference Include="Dapper" Version="2.1.79" />
```

Giữ nguyên các package hiện tại của `UnitOfWork.Tests`.

- [ ] **Step 2: Tạo helper manager không dùng repository**

```csharp
private static UnitOfWorkManager CreateManager(IDbConnectionFactory database) =>
    new(database, static (_, _) => new object());
```

Repository factory không được gọi trong các test này.

- [ ] **Step 3: Viết happy-path sync test**

```csharp
[Fact]
public async Task QueryMultiple_Reads_All_Grids_Under_One_Operation_Lease()
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

- [ ] **Step 4: Viết async happy-path test**

Dùng `QueryMultipleAsync(CommandDefinition)`, đọc ba grid bằng `ReadSingleAsync<int>()`, dispose bằng `await using`, rồi chạy `SELECT 1` để chứng minh lease đã release.

- [ ] **Step 5: Viết overlap rejection test**

```csharp
[Fact]
public async Task QueryMultiple_Rejects_Another_Command_While_GridReader_Is_Alive()
{
    using var database = new SqliteTestDb();
    var manager = CreateManager(database);
    await using var scope = await manager.BeginAsync();

    await using var grid = await scope.Connection.QueryMultipleAsync(
        "SELECT 11; SELECT 22;");
    Assert.Equal(11, await grid.ReadSingleAsync<int>());

    await Assert.ThrowsAsync<UnitOfWorkConcurrencyException>(
        () => scope.Connection.ExecuteScalarAsync<int>("SELECT 1;"));

    Assert.Equal(22, await grid.ReadSingleAsync<int>());
}
```

- [ ] **Step 6: Viết dispose recovery, early-break và unread-grid tests**

Bao phủ ba trường hợp độc lập:

1. Dispose grid sau grid đầu, sau đó `SELECT 1` thành công.
2. `Read<int>(buffered: false)` chỉ lấy một item rồi break, dispose grid, sau đó command thành công.
3. Không đọc grid thứ hai/thứ ba, dispose grid, sau đó command thành công.

- [ ] **Step 7: Viết nested mapping callback rejection test**

Trong callback Dapper multi-mapping, gọi `scope.Connection.ExecuteScalar<int>("SELECT 1;")` và assert `UnitOfWorkConcurrencyException`. Callback phải chạy khi reader còn active; `GridReader` vẫn được dispose trong `finally`/`using`.

- [ ] **Step 8: Verify RED/GREEN**

```powershell
dotnet test tests/UnitOfWork.Tests/UnitOfWork.Tests.csproj --filter FullyQualifiedName~DapperQueryMultipleTests
```

Expected: toàn bộ test mới pass. Nếu SQLite/Dapper không trả multiple result sets, giữ test RED và xác nhận provider/version trước khi thay fixture; không bỏ contract.

- [ ] **Step 9: Commit**

```powershell
git add tests/UnitOfWork.Tests/UnitOfWork.Tests.csproj tests/UnitOfWork.Tests/DapperQueryMultipleTests.cs
git commit -m "test: cover Dapper QueryMultiple reader lifetime"
```

### Task 2: Thêm fixture deterministic cho async unbuffered cancellation

**Files:**
- Create: `tests/UnitOfWork.Tests/Fixtures/ScriptedMultiResultDbConnection.cs`
- Create: `tests/UnitOfWork.Tests/Fixtures/ScriptedMultiResultDbCommand.cs`
- Create: `tests/UnitOfWork.Tests/Fixtures/ScriptedMultiResultDbDataReader.cs`
- Create: `tests/UnitOfWork.Tests/Fixtures/ScriptedDbTransaction.cs`
- Modify: `tests/UnitOfWork.Tests/DapperQueryMultipleTests.cs`

**Interfaces:**
- Produces: `ScriptedMultiResultDbConnection : DbConnection, IDbConnectionFactory`.
- Produces: `ReadStarted : Task` và `ReleaseRead()`/cancellation-aware `ReadAsync` để test biết reader đã bắt đầu chờ.
- Produces: DQM-07 deterministic, không dựa vào tốc độ SQLite.

- [ ] **Step 1: Implement fake connection/transaction tối thiểu**

`CreateConnection()` trả chính connection mới ở trạng thái open. `BeginDbTransactionAsync` trả `ScriptedDbTransaction`. Không implement behavior ngoài những gì Dapper và UnitOfWork cần.

- [ ] **Step 2: Implement command trả scripted result sets**

Constructor nhận các result set dạng:

```csharp
IReadOnlyList<IReadOnlyList<object?[]>> resultSets
```

`ExecuteDbDataReader` và `ExecuteDbDataReaderAsync` trả `ScriptedMultiResultDbDataReader` trên cùng dữ liệu.

- [ ] **Step 3: Implement reader state machine**

Reader phải hỗ trợ tối thiểu:

```csharp
public override bool Read();
public override Task<bool> ReadAsync(CancellationToken cancellationToken);
public override bool NextResult();
public override Task<bool> NextResultAsync(CancellationToken cancellationToken);
public override object GetValue(int ordinal);
public override int FieldCount { get; }
public override string GetName(int ordinal);
public override Type GetFieldType(int ordinal);
```

Khi `BlockNextRead` được bật, `ReadAsync` set `ReadStarted`, chờ `TaskCompletionSource`, và honor cancellation token bằng `WaitAsync(cancellationToken)`.

- [ ] **Step 4: Viết cancellation test**

```csharp
[Fact]
public async Task QueryMultiple_ReadUnbufferedAsync_Cancellation_Does_Not_Leak_Lease()
{
    var database = ScriptedMultiResultDbConnection.CreateBlockingReaderFixture();
    var manager = CreateManager(database);
    await using var scope = await manager.BeginAsync();

    using var cts = new CancellationTokenSource();
    await using (var grid = await scope.Connection.QueryMultipleAsync(
        new CommandDefinition("script", cancellationToken: cts.Token)))
    {
        var enumeration = ConsumeAsync(grid.ReadUnbufferedAsync<int>());
        await database.ReadStarted;
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumeration);
    }

    Assert.Equal(1, await scope.Connection.ExecuteScalarAsync<int>("scalar-one"));
    await scope.RollbackAsync();
}
```

`ConsumeAsync` phải enumerate thực sự; không chỉ tạo `IAsyncEnumerable`.

- [ ] **Step 5: Verify**

```powershell
dotnet test tests/UnitOfWork.Tests/UnitOfWork.Tests.csproj --filter FullyQualifiedName~DapperQueryMultipleTests
```

Expected: DQM-01 đến DQM-08 pass, không deadlock.

- [ ] **Step 6: Commit**

```powershell
git add tests/UnitOfWork.Tests
git commit -m "test: verify QueryMultiple async cancellation cleanup"
```

### Task 3: Bổ sung RepoDb SQLite cold/hot metadata regression tests

**Files:**
- Modify: `tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests.csproj`
- Create: `tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests/AssemblyInfo.cs`
- Create: `tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests/RepoDbSqliteMetadataTests.cs`
- Create: `tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests/Fixtures/RepoDbSqliteTestDatabase.cs`
- Create: `tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests/Fixtures/CountingDbHelper.cs`
- Create: `tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests/Fixtures/InjectedDbHelper.cs`

**Interfaces:**
- Produces: direct integration tests RSQL-01 đến RSQL-06, độc lập HTTP sample test.
- Consumes: `TransactionBoundSqliteDbHelper` hiện có trong sample project.

- [ ] **Step 1: Thêm explicit test dependencies và tắt parallelization**

Thêm explicit references nếu test project chưa có trực tiếp:

```xml
<PackageReference Include="RepoDb" Version="1.15.1" />
<PackageReference Include="RepoDb.Sqlite.Microsoft" Version="1.15.0" />
```

Tạo:

```csharp
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
```

- [ ] **Step 2: Tạo SQLite fixture có hai table**

Schema:

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

Fixture dùng file riêng, `Pooling=False`, implement `IDbConnectionFactory`, và có `CountAsync(tableName)` bằng raw independent connection sau commit.

- [ ] **Step 3: Tạo `CountingDbHelper`**

Implement đầy đủ `IDbHelper`, delegate sang helper thật, và increment dictionary theo normalized table name trong `GetFields`/`GetFieldsAsync`. `GetScopeIdentity*` và `DynamicHandler` chỉ delegate, không đổi semantics.

- [ ] **Step 4: Tạo `InjectedDbHelper`**

Cho phép inject callback trước helper thật:

```csharp
Func<IDbConnection, string, IDbTransaction?, CancellationToken, Task>? BeforeGetFieldsAsync
```

Callback dùng transaction-bound connection được truyền vào. Mọi member không inject phải delegate sang helper thật.

- [ ] **Step 5: Đăng ký mapping SQLite giống production sample**

```csharp
GlobalConfiguration.Setup().UseSqlite();
DbSettingMapper.Add<TransactionBoundDbConnection>(
    DbSettingMapper.Get<SqliteConnection>(), true);
StatementBuilderMapper.Add<TransactionBoundDbConnection>(
    StatementBuilderMapper.Get<SqliteConnection>(), true);
```

Mỗi test tự đăng ký `DbHelperMapper` với counting/injected wrapper mong muốn và gọi `DbFieldCache.Flush()` trước hành vi cold-cache.

- [ ] **Step 6: Viết cold/hot và two-table tests**

Cold/hot sequence bắt buộc:

```text
Flush cache
→ begin root
→ insert counter value 10
→ commit
→ begin root mới, không flush
→ insert counter value 20
→ commit
→ helper count(counter) == 1
→ persisted rows == 2
```

Two-table test flush một lần, insert tuần tự vào `counter` rồi `counter_archive` trong cùng root, và assert helper count của mỗi table bằng 1.

- [ ] **Step 7: Viết identity test**

Dùng overload RepoDb trả identity:

```csharp
var identity = await scope.Connection.InsertAsync(
    "counter",
    new { value = 42 });

Assert.True(Convert.ToInt64(identity) > 0);
```

Commit rồi kiểm tra row persist bằng independent connection.

- [ ] **Step 8: Viết deterministic discovery cancellation test**

`InjectedDbHelper.BeforeGetFieldsAsync` phải:

1. Set `operationStarted`.
2. Tạo command trên transaction-bound connection.
3. Chạy một command chờ cancellation bằng test-only SQLite function hoặc scripted command fixture.
4. Honor token và throw cancellation.

Gọi trực tiếp `mappedHelper.GetFieldsAsync(scope.Connection, "counter", null, token)` để token đi vào helper. Sau cancellation, restore helper thật và chạy `SELECT 1` trên cùng scope; scalar phải thành công.

- [ ] **Step 9: Viết discovery failure-after-reader test**

Injected callback mở reader `SELECT 1`, đọc một row, rồi throw `InvalidOperationException("Injected metadata failure")` bên trong `await using`. Sau exception, restore helper thật và chạy scalar trên cùng scope; scalar phải thành công.

- [ ] **Step 10: Verify**

```powershell
dotnet test tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests.csproj --filter FullyQualifiedName~RepoDbSqliteMetadataTests
```

Expected: RSQL-01 đến RSQL-06 pass và HTTP integration test cũ vẫn pass khi chạy toàn project.

- [ ] **Step 11: Commit**

```powershell
git add tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests
git commit -m "test: cover RepoDb SQLite metadata discovery lifecycle"
```

### Task 4: Tạo shared provider contract library

**Files:**
- Create: `tests/UnitOfWork.RepoDb.ProviderTests.Shared/UnitOfWork.RepoDb.ProviderTests.Shared.csproj`
- Create: `tests/UnitOfWork.RepoDb.ProviderTests.Shared/IRepoDbProviderHarness.cs`
- Create: `tests/UnitOfWork.RepoDb.ProviderTests.Shared/RepoDbProviderContract.cs`
- Create: `tests/UnitOfWork.RepoDb.ProviderTests.Shared/CountingDbHelper.cs`
- Create: `tests/UnitOfWork.RepoDb.ProviderTests.Shared/InjectedDbHelper.cs`
- Create: `tests/UnitOfWork.RepoDb.ProviderTests.Shared/ExternalDatabaseFactAttribute.cs`

**Interfaces:**
- Produces: provider-neutral contract methods RPM-01 đến RPM-08.
- Provider projects chỉ chịu trách nhiệm concrete connection, bootstrap/mapping và schema SQL.

- [ ] **Step 1: Tạo shared project**

Target `net8.0`, reference `UnitOfWork.Core`, RepoDb 1.15.1 và xUnit 2.9.2. Không reference provider-specific package.

- [ ] **Step 2: Định nghĩa harness contract**

```csharp
public interface IRepoDbProviderHarness : IAsyncDisposable
{
    string ProviderName { get; }
    string ScalarOneSql { get; }
    string SlowScalarSql { get; }

    void RegisterMappings(IDbHelper helperOverride);
    IDbHelper GetOfficialHelper();
    DbConnection CreateOpenConnection();
    IDbConnectionFactory CreateConnectionFactory();
    Task ResetSchemaAsync(CancellationToken cancellationToken = default);
    Task<long> CountRowsAsync(string tableName, CancellationToken cancellationToken = default);
}
```

`RegisterMappings` phải luôn đăng ký đủ setting/helper/statement builder cho `TransactionBoundDbConnection`.

- [ ] **Step 3: Implement environment-aware fact attribute**

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

- [ ] **Step 4: Implement shared helper wrappers**

Reuse semantics từ SQLite task: counting per table và injected async callback. Không swallow provider exception.

- [ ] **Step 5: Implement manager factory trong contract**

```csharp
private static UnitOfWorkManager CreateManager(IRepoDbProviderHarness harness) =>
    new(harness.CreateConnectionFactory(), static (_, _) => new object());
```

- [ ] **Step 6: Implement RPM-01/RPM-02 cold-hot contract**

Flow:

```text
Reset schema
Get official helper
Wrap with CountingDbHelper
Register mappings
DbFieldCache.Flush
Insert counter 10 in root 1, commit
Insert counter 20 in root 2 without flush, commit
Assert helper count(counter) == 1
Assert persisted count == 2
```

- [ ] **Step 7: Implement RPM-03/RPM-04**

Two-table contract inserts into both tables in one root. Identity contract asserts returned identity converts to `long > 0`, commits, and verifies persisted row.

- [ ] **Step 8: Implement RPM-05 cancellation probe**

Injected helper callback creates command with `harness.SlowScalarSql`, signals `started`, then calls `ExecuteScalarAsync(token)`. Test waits `started`, cancels, accepts provider cancellation exception but explicitly rejects `UnitOfWorkConcurrencyException`, restores official helper, then runs `ScalarOneSql` successfully on the same scope.

- [ ] **Step 9: Implement RPM-06 failure-after-reader probe**

Injected helper callback opens reader using `ScalarOneSql`, reads one row, then throws injected exception inside `await using`. Sau exception, restore official helper and verify scalar succeeds on same scope.

- [ ] **Step 10: Implement RPM-07 overlap guard contract**

Open ADO.NET reader on `SELECT id, value FROM counter`, keep reader alive, then call RepoDb insert on same scope. Assert exact `UnitOfWorkConcurrencyException`. Dispose reader, retry insert, and assert retry succeeds.

- [ ] **Step 11: Implement RPM-08 independent roots contract**

Create two `UnitOfWorkManager` instances from the same harness. Start one root per manager, synchronize start bằng barrier, chạy insert đồng thời, complete cả hai, và assert persisted count tăng 2. Không dùng cùng manager trong cùng ambient flow.

- [ ] **Step 12: Commit**

```powershell
git add tests/UnitOfWork.RepoDb.ProviderTests.Shared
git commit -m "test: add shared RepoDb provider compatibility contracts"
```

### Task 5: Thêm SQL Server provider test project

**Files:**
- Create: `tests/UnitOfWork.RepoDb.SqlServer.Tests/UnitOfWork.RepoDb.SqlServer.Tests.csproj`
- Create: `tests/UnitOfWork.RepoDb.SqlServer.Tests/AssemblyInfo.cs`
- Create: `tests/UnitOfWork.RepoDb.SqlServer.Tests/SqlServerProviderHarness.cs`
- Create: `tests/UnitOfWork.RepoDb.SqlServer.Tests/ProviderContractTests.cs`

**Interfaces:**
- Consumes environment variable: `UOW_TEST_SQLSERVER`.
- Uses `Microsoft.Data.SqlClient.SqlConnection` and `RepoDb.SqlServer` 1.15.0.

- [ ] **Step 1: Tạo test project**

Reference test SDK/xUnit/coverlet, RepoDb 1.15.1, RepoDb.SqlServer 1.15.0, shared project và `UnitOfWork.Core`.

- [ ] **Step 2: Tắt test parallelization**

```csharp
[assembly: CollectionBehavior(DisableTestParallelization = true)]
```

- [ ] **Step 3: Implement schema reset**

```sql
IF OBJECT_ID('dbo.counter_archive', 'U') IS NOT NULL DROP TABLE dbo.counter_archive;
IF OBJECT_ID('dbo.counter', 'U') IS NOT NULL DROP TABLE dbo.counter;
CREATE TABLE dbo.counter (
    id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    value INT NOT NULL
);
CREATE TABLE dbo.counter_archive (
    id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    value INT NOT NULL
);
```

- [ ] **Step 4: Register SQL Server mappings**

```csharp
GlobalConfiguration.Setup().UseSqlServer();
DbSettingMapper.Add<TransactionBoundDbConnection>(DbSettingMapper.Get<SqlConnection>(), true);
DbHelperMapper.Add<TransactionBoundDbConnection>(helperOverride, true);
StatementBuilderMapper.Add<TransactionBoundDbConnection>(StatementBuilderMapper.Get<SqlConnection>(), true);
```

- [ ] **Step 5: Provider SQL constants**

```csharp
ScalarOneSql = "SELECT 1;";
SlowScalarSql = "WAITFOR DELAY '00:00:30'; SELECT 1;";
```

- [ ] **Step 6: Wire eight facts**

Mỗi fact dùng `[ExternalDatabaseFact("UOW_TEST_SQLSERVER")]`, tạo harness mới, và gọi đúng một method RPM trong shared contract.

- [ ] **Step 7: Run with configured database**

```powershell
$env:UOW_TEST_SQLSERVER = "Server=127.0.0.1,14333;Database=uow_tests;User Id=sa;Password=<local-test-password>;Encrypt=False;TrustServerCertificate=True"
dotnet test tests/UnitOfWork.RepoDb.SqlServer.Tests/UnitOfWork.RepoDb.SqlServer.Tests.csproj
```

Expected: 8 passed, 0 failed, 0 skipped.

- [ ] **Step 8: Commit**

```powershell
git add tests/UnitOfWork.RepoDb.SqlServer.Tests
git commit -m "test: add RepoDb SQL Server compatibility matrix"
```

### Task 6: Thêm PostgreSQL provider test project

**Files:**
- Create: `tests/UnitOfWork.RepoDb.PostgreSql.Tests/UnitOfWork.RepoDb.PostgreSql.Tests.csproj`
- Create: `tests/UnitOfWork.RepoDb.PostgreSql.Tests/AssemblyInfo.cs`
- Create: `tests/UnitOfWork.RepoDb.PostgreSql.Tests/PostgreSqlProviderHarness.cs`
- Create: `tests/UnitOfWork.RepoDb.PostgreSql.Tests/ProviderContractTests.cs`

**Interfaces:**
- Consumes environment variable: `UOW_TEST_POSTGRESQL`.
- Uses `NpgsqlConnection` and RepoDb.PostgreSql 1.15.0.

- [ ] **Step 1: Tạo project và tắt parallelization**

Dùng cùng test package versions như SQL Server project.

- [ ] **Step 2: Implement schema reset**

```sql
DROP TABLE IF EXISTS counter_archive;
DROP TABLE IF EXISTS counter;
CREATE TABLE counter (
    id BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    value INTEGER NOT NULL
);
CREATE TABLE counter_archive (
    id BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    value INTEGER NOT NULL
);
```

- [ ] **Step 3: Register PostgreSQL mappings**

Bootstrap RepoDb PostgreSQL, rồi map `TransactionBoundDbConnection` sang `NpgsqlConnection` setting/helper/statement builder. Không dùng helper fallback connection mới trong test harness.

- [ ] **Step 4: Provider SQL constants**

```csharp
ScalarOneSql = "SELECT 1;";
SlowScalarSql = "SELECT pg_sleep(30);";
```

- [ ] **Step 5: Wire và chạy 8 facts**

```powershell
$env:UOW_TEST_POSTGRESQL = "Host=127.0.0.1;Port=15432;Database=uow_tests;Username=postgres;Password=<local-test-password>"
dotnet test tests/UnitOfWork.RepoDb.PostgreSql.Tests/UnitOfWork.RepoDb.PostgreSql.Tests.csproj
```

Expected: 8 passed, 0 failed, 0 skipped.

- [ ] **Step 6: Commit**

```powershell
git add tests/UnitOfWork.RepoDb.PostgreSql.Tests
git commit -m "test: add RepoDb PostgreSQL compatibility matrix"
```

### Task 7: Thêm MySql.Data provider test project

**Files:**
- Create: `tests/UnitOfWork.RepoDb.MySql.Tests/UnitOfWork.RepoDb.MySql.Tests.csproj`
- Create: `tests/UnitOfWork.RepoDb.MySql.Tests/AssemblyInfo.cs`
- Create: `tests/UnitOfWork.RepoDb.MySql.Tests/MySqlProviderHarness.cs`
- Create: `tests/UnitOfWork.RepoDb.MySql.Tests/ProviderContractTests.cs`

**Interfaces:**
- Consumes environment variable: `UOW_TEST_MYSQL`.
- Uses `MySql.Data.MySqlClient.MySqlConnection` và RepoDb.MySql 1.15.0.

- [ ] **Step 1: Tạo project và tắt parallelization**

Reference shared provider contract và provider package.

- [ ] **Step 2: Implement schema reset**

```sql
DROP TABLE IF EXISTS counter_archive;
DROP TABLE IF EXISTS counter;
CREATE TABLE counter (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    value INT NOT NULL
) ENGINE=InnoDB;
CREATE TABLE counter_archive (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    value INT NOT NULL
) ENGINE=InnoDB;
```

- [ ] **Step 3: Register MySql.Data mappings**

Bootstrap RepoDb MySQL và map ba mapper từ concrete `MySql.Data.MySqlClient.MySqlConnection` sang `TransactionBoundDbConnection`.

- [ ] **Step 4: Provider SQL constants**

```csharp
ScalarOneSql = "SELECT 1;";
SlowScalarSql = "SELECT SLEEP(30);";
```

- [ ] **Step 5: Wire và chạy 8 facts**

```powershell
$env:UOW_TEST_MYSQL = "Server=127.0.0.1;Port=13306;Database=uow_tests;User ID=root;Password=<local-test-password>;SslMode=None"
dotnet test tests/UnitOfWork.RepoDb.MySql.Tests/UnitOfWork.RepoDb.MySql.Tests.csproj
```

Expected: 8 passed, 0 failed, 0 skipped.

- [ ] **Step 6: Commit**

```powershell
git add tests/UnitOfWork.RepoDb.MySql.Tests
git commit -m "test: add RepoDb MySql.Data compatibility matrix"
```

### Task 8: Thêm MySqlConnector provider test project

**Files:**
- Create: `tests/UnitOfWork.RepoDb.MySqlConnector.Tests/UnitOfWork.RepoDb.MySqlConnector.Tests.csproj`
- Create: `tests/UnitOfWork.RepoDb.MySqlConnector.Tests/AssemblyInfo.cs`
- Create: `tests/UnitOfWork.RepoDb.MySqlConnector.Tests/MySqlConnectorProviderHarness.cs`
- Create: `tests/UnitOfWork.RepoDb.MySqlConnector.Tests/ProviderContractTests.cs`

**Interfaces:**
- Consumes environment variable: `UOW_TEST_MYSQL`.
- Uses `MySqlConnector.MySqlConnection` và RepoDb.MySqlConnector 1.15.0.

- [ ] **Step 1: Tạo project riêng**

Không đặt MySql.Data và MySqlConnector trong cùng project vì hai provider bootstrap/mapping cùng nhắm `TransactionBoundDbConnection` và dùng type `MySqlConnection` có namespace khác nhau.

- [ ] **Step 2: Reuse cùng MySQL schema/connection string**

Harness reset hai table trước mỗi test và dùng InnoDB.

- [ ] **Step 3: Register MySqlConnector mappings**

Map setting/helper/statement builder từ `MySqlConnector.MySqlConnection` sang `TransactionBoundDbConnection`.

- [ ] **Step 4: Wire và chạy 8 facts**

```powershell
$env:UOW_TEST_MYSQL = "Server=127.0.0.1;Port=13306;Database=uow_tests;User ID=root;Password=<local-test-password>;SslMode=None"
dotnet test tests/UnitOfWork.RepoDb.MySqlConnector.Tests/UnitOfWork.RepoDb.MySqlConnector.Tests.csproj
```

Expected: 8 passed, 0 failed, 0 skipped.

- [ ] **Step 5: Commit**

```powershell
git add tests/UnitOfWork.RepoDb.MySqlConnector.Tests
git commit -m "test: add RepoDb MySqlConnector compatibility matrix"
```

### Task 9: Thêm Docker Compose và verification script

**Files:**
- Create: `tests/containers/repodb-provider-matrix.compose.yml`
- Create: `scripts/verify-repodb-dapper-compatibility.ps1`

**Interfaces:**
- Produces: một command local khởi động database, chạy local contracts + provider matrix, kiểm tra skipped count, và cleanup.

- [ ] **Step 1: Định nghĩa containers và fixed local ports**

Compose services:

```text
uow-sqlserver  -> 14333:1433, SQL Server 2022
uow-postgresql -> 15432:5432, PostgreSQL 16
uow-mysql      -> 13306:3306, MySQL 8.4
```

Dùng test-only credentials cố định trong compose; ghi rõ không dùng cho production. PostgreSQL/MySQL tạo sẵn database `uow_tests`. SQL Server database được tạo bởi verification script sau health check.

- [ ] **Step 2: Thêm health checks**

Health check phải dùng command native trong container. Script không được bắt đầu test trước khi cả ba service healthy.

- [ ] **Step 3: Implement PowerShell parameters**

```powershell
param(
    [switch]$KeepContainers,
    [int]$SqlServerPort = 14333,
    [int]$PostgreSqlPort = 15432,
    [int]$MySqlPort = 13306
)
```

Script fail-fast nếu `docker`, `docker compose` hoặc `dotnet` không khả dụng.

- [ ] **Step 4: Chạy local always-on tests trước**

```powershell
dotnet test tests/UnitOfWork.Tests/UnitOfWork.Tests.csproj --filter FullyQualifiedName~DapperQueryMultipleTests
dotnet test tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests.csproj --filter FullyQualifiedName~RepoDbSqliteMetadataTests
```

- [ ] **Step 5: Start containers và tạo SQL Server database**

Dùng:

```powershell
docker compose -f tests/containers/repodb-provider-matrix.compose.yml up -d --wait
```

Sau đó chạy `sqlcmd` bên trong SQL Server container để tạo `uow_tests` nếu chưa tồn tại.

- [ ] **Step 6: Set process environment variables**

Set `UOW_TEST_SQLSERVER`, `UOW_TEST_POSTGRESQL`, `UOW_TEST_MYSQL` bằng ports parameters. Không ghi password ra log ngoài test-only values đã có trong compose.

- [ ] **Step 7: Chạy từng provider project và xuất TRX riêng**

```powershell
dotnet test tests/UnitOfWork.RepoDb.SqlServer.Tests/UnitOfWork.RepoDb.SqlServer.Tests.csproj --logger "trx;LogFileName=repodb-sqlserver.trx"
dotnet test tests/UnitOfWork.RepoDb.PostgreSql.Tests/UnitOfWork.RepoDb.PostgreSql.Tests.csproj --logger "trx;LogFileName=repodb-postgresql.trx"
dotnet test tests/UnitOfWork.RepoDb.MySql.Tests/UnitOfWork.RepoDb.MySql.Tests.csproj --logger "trx;LogFileName=repodb-mysql.trx"
dotnet test tests/UnitOfWork.RepoDb.MySqlConnector.Tests/UnitOfWork.RepoDb.MySqlConnector.Tests.csproj --logger "trx;LogFileName=repodb-mysqlconnector.trx"
```

- [ ] **Step 8: Parse TRX counters**

Mỗi TRX phải có:

```text
failed = 0
skipped/notExecuted = 0
passed = 8
```

Script fail nếu provider project bị skip vì env var/misconfiguration.

- [ ] **Step 9: Cleanup trong finally**

Nếu không có `-KeepContainers`, chạy:

```powershell
docker compose -f tests/containers/repodb-provider-matrix.compose.yml down -v
```

Cleanup phải chạy kể cả test fail.

- [ ] **Step 10: Commit**

```powershell
git add tests/containers scripts/verify-repodb-dapper-compatibility.ps1
git commit -m "test: add provider compatibility verification environment"
```

### Task 10: Tích hợp solution, documentation và full verification

**Files:**
- Modify: `UnitOfWork.slnx`
- Modify: `README.md`

**Interfaces:**
- Produces: test projects discoverable trong solution và hướng dẫn chạy local/default/provider matrix.

- [ ] **Step 1: Thêm projects vào solution**

Trong `/tests/` thêm shared project và bốn provider test projects. Shared project là library, bốn project còn lại là test projects.

- [ ] **Step 2: Document test tiers trong README**

Ghi rõ:

1. `dotnet test UnitOfWork.slnx` chạy local tests và skip external provider facts khi env vars không có.
2. `scripts/verify-repodb-dapper-compatibility.ps1` là verification đầy đủ cần Docker.
3. RepoDb mapping/cache static là lý do provider test tách process.
4. `GridReader` còn sống đồng nghĩa root operation lease còn bị giữ.

- [ ] **Step 3: Restore/build solution**

```powershell
dotnet restore UnitOfWork.slnx
dotnet build UnitOfWork.slnx --no-restore --warnaserror
```

Expected: không warning, không error.

- [ ] **Step 4: Run default solution tests without Docker**

```powershell
dotnet test UnitOfWork.slnx --no-build --logger "console;verbosity=normal"
```

Expected: local tests pass; external provider facts có thể skip với message chứa đúng environment variable.

- [ ] **Step 5: Run full compatibility verification**

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

- [ ] **Step 6: Run existing regression scripts**

```powershell
powershell -ExecutionPolicy Bypass -File scripts/verify-p0-transaction-invariants.ps1
powershell -ExecutionPolicy Bypass -File scripts/verify-p1-async-concurrency-boundary.ps1
```

Expected: cả hai script vẫn pass.

- [ ] **Step 7: Final commit**

```powershell
git add UnitOfWork.slnx README.md
git commit -m "docs: document RepoDb and Dapper compatibility tests"
```

---

## Definition of Done

- DQM-01 đến DQM-08 có test deterministic và pass.
- RSQL-01 đến RSQL-06 có test direct integration và pass.
- Mỗi provider SQL Server/PostgreSQL/MySql.Data/MySqlConnector chạy RPM-01 đến RPM-08 trong process riêng.
- Cold-cache test thực sự gọi `DbFieldCache.Flush()` và hot-cache test assert helper invocation count.
- Cancellation/failure probe chứng minh command sau vẫn chạy được trên cùng scope, tức operation lease không leak.
- Same-root overlap bị `UnitOfWorkConcurrencyException`; independent roots chạy đồng thời thành công.
- Default solution test không cần Docker.
- Full compatibility script cần Docker và fail nếu bất kỳ provider test nào skip.
- Không có thay đổi production code hoặc nới lỏng concurrency guard trong implementation của plan này.
- README giải thích rõ behavior của RepoDb metadata discovery và Dapper `GridReader` lifetime.
