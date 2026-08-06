# UnitOfWork.Core

Thư viện mẫu triển khai Unit of Work trên `DbConnection`/`DbTransaction`.

## Contract giao dịch P0

`UnitOfWorkManager.BeginAsync()` trả về một `IUnitOfWorkScope` riêng biệt cho
mỗi lần gọi, kể cả khi begin được lồng nhau. Các scope đó là những lease độc lập
trên cùng một root transaction và repository cache khi chúng thuộc cùng manager
và execution context. Begin lồng nhau không trả lại cùng một owner/scope.

- `CompleteAsync()` chỉ đánh dấu scope hiện tại là thành công và finalizes scope
  đó đúng một lần; nó không tự commit root transaction khi vẫn còn scope khác.
- Dispose một scope chưa complete sẽ yêu cầu rollback root transaction. Do đó,
  một scope trong incomplete vẫn làm outer scope rollback khi root được settle.
- Ứng dụng async nên dùng `await using` để rollback/commit cleanup đi qua API
  async thật của provider. `Dispose()` chỉ là compatibility fallback đồng bộ.
- Dispose scope trong không sở hữu, dispose, hoặc giải phóng raw root connection
  hay transaction; chỉ root finalization mới làm việc đó.
- `manager.Current` là context view (`IUnitOfWorkContext`) của root hiện tại,
  không phải ownership lease và không phải scope đã được begin trả về.
- Manager tự động xóa current context sau khi root được finalize, sau lỗi khởi
  tạo, và sau cleanup/finalization failure. Không gọi `ClearCurrent()` thủ công.
- Connection được cung cấp cho context bị ràng buộc vào transaction root.
  Command tạo từ connection đó tự enlist vào transaction, vì vậy repository và
  data-access library không được gán `command.Transaction` thủ công.
- Ambient root được cô lập theo từng `UnitOfWorkManager`; hai manager không chia
  sẻ current root, transaction, hay repository cache.

## Chính sách execution flow và concurrency

`AsyncLocal` truyền current root vào child task kế thừa execution context. Những
task đó có thể dùng root tuần tự khi root còn active, nhưng không thể chạy hai
command dùng chung root cùng lúc: operation lease sẽ reject overlap ngay lập tức.
`ExecutionContext.SuppressFlow()` không kế thừa parent root. Trong task đó,
truy cập hoặc settle một parent scope bị giữ lại sẽ bị reject vì current root bị
missing/foreign; tuy nhiên `BeginAsync()` không có ambient root sẽ tạo một UoW
độc lập hợp lệ. Scope giữ lại sau khi root finalize vẫn bị reject theo lifecycle
state. Thư viện không cấm mọi `Task.Run`; nó cấm shared operation song song và
mọi usage ngoài active lifecycle.

Repository cache được tạo đúng một lần dưới root lifecycle lock. Repository
factory là constructor-only, đồng bộ và không làm I/O/`await`; nếu factory ném
lỗi, instance/key chưa được cache và lần gọi tiếp theo có thể thử lại. Lock order
luôn là `scope settlement lock -> root lifecycle lock -> operation flag`.

Mẫu async dùng options và cancellation:

```csharp
using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var options = new UnitOfWorkOptions
{
    IsolationLevel = IsolationLevel.ReadCommitted,
    CommandTimeoutSeconds = 15
};

await using var scope = await manager.BeginAsync(options, cancellation.Token);

await using var command = scope.Connection.CreateCommand();
command.CommandText = "UPDATE Counter SET Value = Value + 1 WHERE Id = 1;";
await command.ExecuteNonQueryAsync(cancellation.Token);

await scope.CompleteAsync(cancellation.Token);
```

Tương thích sync vẫn được giữ cho code cũ; nó chỉ là fallback khi provider/API
async không thể dùng:

```csharp
using var scope = await manager.BeginAsync();
using var command = scope.Connection.CreateCommand();
command.CommandText = "UPDATE Counter SET Value = Value + 1 WHERE Id = 1;";
command.ExecuteNonQuery();
scope.CompleteAsync().GetAwaiter().GetResult();
```

Reader streaming giữ operation lease cho đến khi được đóng/dispose. Không chạy
command khác, complete, hay rollback đồng thời trên cùng root khi reader đang
mở; luôn dispose reader trước:

```csharp
await using var reader = await command.ExecuteReaderAsync(cancellation.Token);
while (await reader.ReadAsync(cancellation.Token))
{
    // stream rows
}
// reader is disposed here; the next operation may now start.
```

Không dùng fail-fast `Task.WhenAll` cho nhiều command trên cùng scope:

```csharp
// Sai: shared root chỉ cho một operation tại một thời điểm.
await Task.WhenAll(command1.ExecuteNonQueryAsync(), command2.ExecuteNonQueryAsync());
```

Task con kế thừa `ExecutionContext` cũng kế thừa ambient root. Chúng có thể dùng
root **tuần tự** khi root còn active, nhưng operation đồng thời bị reject ngay
lập tức. Task dùng `ExecutionContext.SuppressFlow()` không kế thừa parent root.
Việc access hoặc settle parent scope bị giữ lại sẽ bị reject, nhưng `BeginAsync()`
trong task đó tạo một UoW độc lập hợp lệ; một scope bị giữ lại sau root
finalization cũng bị reject.

Factory repository có chữ ký `Func<Type, DbConnection, object>`, được gọi dưới
root lifecycle lock, chỉ tạo object đồng bộ và **không làm I/O/`await`**. Nếu
factory ném lỗi thì không cache instance/key, nên lần sau có thể thử lại.

`CommandBehavior.CloseConnection` bị từ chối vì root sở hữu connection;
`DbBatch` cũng không được hỗ trợ trên connection transaction-bound. Không tự
gán `command.Transaction`: command được tạo từ `scope.Connection` đã enlist
vào root transaction.

Transaction factory mặc định chỉ chuyển `IsolationLevel` sang provider.
`ReadOnly` và `TransactionTimeout` fail-fast để tránh hứa một behavior không
portable. Provider nào hỗ trợ chúng phải được inject factory riêng:

```csharp
var manager = new UnitOfWorkManager(connectionFactory, repositoryFactory,
    new ProviderSpecificTransactionFactory());

await using var scope = await manager.BeginAsync(new UnitOfWorkOptions
{
    ReadOnly = true,
    TransactionTimeout = TimeSpan.FromSeconds(10)
});
```

`ProviderSpecificTransactionFactory` triển khai `IUnitOfWorkTransactionFactory`
và nhận đầy đủ `UnitOfWorkOptions` cùng `CancellationToken`; nó chịu trách nhiệm
dịch read-only/timeout sang API riêng của provider.

### Failure và cancellation policy

| Tình huống | Outcome | Retry policy |
|---|---|---|
| Command bị cancel/lỗi trước completion | Operation lease được release; scope còn active và có thể `RollbackAsync()` | Có thể retry command theo policy ứng dụng nếu provider cho phép |
| Reader creation bị cancel/lỗi | Lease được release; scope còn active | Có thể rollback hoặc thử reader/command khác tuần tự |
| Token đã bị cancel trước khi gọi `CompleteAsync` / `RollbackAsync` | Ném cancellation trước khi đổi scope/root; scope và root vẫn active | Có thể gọi lại trên cùng scope với token còn hiệu lực |
| `CompleteAsync` bị cancel/lỗi sau khi finalization bắt đầu | Root faulted/finalized; không được retry completion | Không retry trên cùng scope/root; bắt đầu UoW mới |
| `RollbackAsync` bị cancel/lỗi sau khi finalization bắt đầu | Root faulted/finalized; không được retry rollback | Không retry trên cùng scope/root; bắt đầu UoW mới |
| Cleanup lỗi sau commit thành công | Commit outcome vẫn được giữ; ambient luôn bị clear | Không retry commit; quyết định recovery ngoài UoW |
| `DisposeAsync()` khi chưa complete | Rollback async rõ ràng trước khi dispose resource | Không dùng lại scope/root |

## Migration từ API cũ

| Before P1 | After P1 |
|---|---|
| `IDbConnection` public/factory | `DbConnection` public/factory |
| `Func<Type, IDbConnection, object>` | `Func<Type, DbConnection, object>` |
| `BeginAsync()` | `BeginAsync(options, cancellationToken)` |
| scope chỉ `IDisposable` | scope `IDisposable` + `IAsyncDisposable` |
| command async không có | `DbCommand.Execute*Async` thật |
| reader trả raw provider reader | transaction-bound reader giữ operation lease |
| provider default transaction config | `UnitOfWorkOptions` + transaction factory |

Các đổi tên P0 vẫn áp dụng: `IUnitOfWork` thành `IUnitOfWorkScope` /
`IUnitOfWorkContext`, `CommitAsync()` thành `CompleteAsync()`, bỏ
`ClearCurrent()`, và bỏ `command.Transaction = transaction`.

## Samples

### Console + SQLite

Sample runnable tại `samples/UnitOfWork.Sample.Console` dùng ADO.NET và SQLite
file-based thật để minh họa trực tiếp lifecycle, nested scope, rollback,
repository cache, timeout, cancellation và concurrency guard.

```powershell
dotnet run --project samples/UnitOfWork.Sample.Console/UnitOfWork.Sample.Console.csproj
```

Xem giải thích và output mong đợi tại
[`samples/UnitOfWork.Sample.Console/README.md`](samples/UnitOfWork.Sample.Console/README.md).

### ASP.NET Core Controllers + Dapper + SQLite

Sample tại `samples/UnitOfWork.Sample.WebApi.Controllers` minh họa cấu trúc gần
ứng dụng doanh nghiệp:

```text
Controller -> Application Service -> Nested Service -> Dapper Repository
```

Sample cung cấp API đọc dữ liệu, commit outer/inner service, rollback do inner
scope incomplete và diagnostics cho repository cache, command timeout,
cancellation/ambient cleanup cùng reader concurrency guard.

```powershell
dotnet run --project samples/UnitOfWork.Sample.WebApi.Controllers/UnitOfWork.Sample.WebApi.Controllers.csproj
```

Xem endpoint, request/response mẫu và quy tắc Dapper tại
[`samples/UnitOfWork.Sample.WebApi.Controllers/README.md`](samples/UnitOfWork.Sample.WebApi.Controllers/README.md).

### ASP.NET Core Minimal API + RepoDb + SQLite

Sample tại `samples/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb` giữ HTTP layer
gọn hơn và minh họa RepoDb trên transaction-bound connection:

```text
Minimal API -> Application Service -> Nested Service -> RepoDb Repository
```

Sample dùng RepoDb entity/raw-SQL operations mà không truyền transaction riêng,
minh họa nested commit, rollback từ inner scope incomplete, repository cache,
timeout, cancellation boundary và reader concurrency guard.

```powershell
dotnet run --project samples/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.csproj
```

Các RepoDb async overload trong sample không nhận `CancellationToken`; repository
kiểm tra token trước/sau operation và Unit of Work lifecycle vẫn nhận request
token trực tiếp. Xem chi tiết tại
[`samples/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb/README.md`](samples/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb/README.md).

## Test integration SQLite

Test dùng database SQLite file-based thật, được tạo bằng tên ngẫu nhiên trong
`Path.GetTempPath()`. Connection string đặt `Pooling=False` và giữ journal mode
mặc định (rollback-journal), nhờ đó connection đọc độc lập thấy snapshot trước
commit mà không cần WAL.

Mỗi logical flow trong test flow-isolation dùng một file database riêng. SQLite
chỉ cho một write transaction trên mỗi file; dùng chung file cho hai UoW đang mở
transaction sẽ tạo `SQLITE_BUSY` và không phải là hành vi mà flow guard cần đo.

Fixture xóa file `.db` và các sidecar `-journal`, `-wal`, `-shm` với retry ngắn
để phát hiện resource leak mà không phụ thuộc đường dẫn workspace hay máy phát
triển cụ thể.

Hai Web API integration test project dùng `WebApplicationFactory<Program>` để
chạy đủ list, commit, rollback và diagnostics qua HTTP trên database riêng của
application host.

## Cấu trúc

```text
samples/UnitOfWork.Sample.Console/
  Infrastructure/SqliteSampleDatabase.cs
  Repositories/CounterRepository.cs
  Services/CounterService.cs
  SampleApplication.cs
  Program.cs

samples/UnitOfWork.Sample.WebApi.Controllers/
  Controllers/CountersController.cs
  Infrastructure/SqliteSampleDatabase.cs
  Repositories/DapperCounterRepository.cs
  Services/CounterApplicationService.cs
  Services/NestedCounterService.cs
  Program.cs

samples/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb/
  Endpoints/CounterEndpoints.cs
  Infrastructure/SqliteSampleDatabase.cs
  Repositories/RepoDbCounterRepository.cs
  Services/CounterApplicationService.cs
  Services/NestedCounterService.cs
  Program.cs

src/UnitOfWork.Core/
  IUnitOfWorkContext.cs
  IUnitOfWorkScope.cs
  RootUnitOfWork.cs
  UnitOfWorkManager.cs
  UnitOfWorkScope.cs

tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests/
  WebApiSampleTests.cs

tests/UnitOfWork.Sample.WebApi.Tests/
  WebApiSampleTests.cs

tests/UnitOfWork.Tests/
  ConsoleSampleTests.cs
  ScopeLifecycleTests.cs
  TransactionInvariantTests.cs
  ManagerIsolationTests.cs
```

## Chạy build, test và kiểm tra invariant

Yêu cầu .NET 8 SDK trở lên:

```powershell
dotnet restore UnitOfWork.slnx
dotnet build UnitOfWork.slnx --no-restore --warnaserror
dotnet test UnitOfWork.slnx --no-build --logger "console;verbosity=normal"
```

Để chạy đầy đủ các kiểm tra P0, bao gồm quét các pattern API cũ:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/verify-p0-transaction-invariants.ps1
```
