# Minimal API + RepoDb + SQLite sample

Sample này minh họa cách dùng `UnitOfWork.Core` trong một ASP.NET Core Minimal
API sử dụng RepoDb và SQLite file-based thật.

## Kiến trúc

```text
Minimal API endpoints
    -> CounterApplicationService
        -> NestedCounterService
        -> ICounterRepository
            -> RepoDbCounterRepository
                -> transaction-bound DbConnection
                    -> SQLite
```

Mỗi endpoint chỉ chịu trách nhiệm binding HTTP và chuyển
`HttpContext.RequestAborted` xuống application service. Application service sở
hữu root Unit of Work; nested service tự mở scope lồng nhau nhưng vẫn dùng chung
root transaction và repository cache trong cùng execution flow.

## Packages

```xml
<PackageReference Include="RepoDb" Version="1.15.1" />
<PackageReference Include="RepoDb.Sqlite.Microsoft" Version="1.15.0" />
```

Provider RepoDb cho Microsoft SQLite kéo `Microsoft.Data.Sqlite` 10.0.9 hoặc mới
hơn và được khởi tạo một lần khi application bắt đầu:

```csharp
RepoDb.SqliteBootstrap.Initialize();
```

## Quy tắc transaction của RepoDb

Repository chỉ nhận `DbConnection` do Unit of Work cung cấp:

```csharp
return new RepoDbCounterRepository(connection);
```

RepoDb operation không nhận tham số `transaction` riêng:

```csharp
await connection.InsertAsync("Counter", new { Value = value });

var items = await connection.ExecuteQueryAsync<CounterItem>(
    "SELECT Id, Value FROM Counter ORDER BY Id;");
```

Không tự gán `DbCommand.Transaction`. Command tạo từ transaction-bound
connection đã được `UnitOfWork.Core` enlist vào root transaction.

## Cancellation

`BeginAsync()` và `CompleteAsync()` nhận request cancellation token trực tiếp.
Các RepoDb async overload được dùng trong sample không có tham số
`CancellationToken`, nên repository kiểm tra cancellation trước và sau operation:

```csharp
cancellationToken.ThrowIfCancellationRequested();
await connection.InsertAsync("Counter", new { Value = value });
cancellationToken.ThrowIfCancellationRequested();
```

Điều này bảo đảm request bị hủy trước operation sẽ không bắt đầu, và request bị
hủy sau operation sẽ không complete transaction. Tuy nhiên, RepoDb command đang
chạy giữa hai lần kiểm tra không thể bị hủy qua các overload này.

## Chạy sample

```powershell
dotnet run --project samples/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.csproj
```

Database được tạo bằng tên ngẫu nhiên trong `Path.GetTempPath()` và được xóa cùng
các sidecar khi application dừng.

## Endpoints

### Đọc dữ liệu đã commit

```http
GET /api/counters
```

Response ban đầu:

```json
[]
```

### Nested commit

```http
POST /api/counters/commit
Content-Type: application/json

{
  "outerValue": 10,
  "innerValue": 20
}
```

Outer và inner scope đều complete. Root transaction commit một lần:

```json
{
  "scenario": "commit",
  "committed": true,
  "items": [
    { "id": 1, "value": 10 },
    { "id": 2, "value": 20 }
  ]
}
```

### Rollback do inner scope incomplete

```http
POST /api/counters/rollback
Content-Type: application/json

{
  "outerValue": 30,
  "innerValue": 40
}
```

Inner scope bị dispose mà không gọi `CompleteAsync()`. Root ghi nhận rollback
request; vì vậy cả `30` và `40` bị loại bỏ dù outer scope được complete:

```json
{
  "scenario": "rollback",
  "committed": false,
  "items": [
    { "id": 1, "value": 10 },
    { "id": 2, "value": 20 }
  ]
}
```

### Diagnostics

```http
GET /api/counters/diagnostics
```

Response mong đợi:

```json
{
  "repositoryCacheReused": true,
  "commandTimeoutSeconds": 15,
  "cancellationObserved": true,
  "ambientClearedAfterCancellation": true,
  "concurrencyGuardObserved": true,
  "allPassed": true
}
```

Diagnostics kiểm tra:

- cùng repository type trong một root trả về cùng instance;
- `CommandTimeoutSeconds = 15` được áp dụng cho command tạo từ scope connection;
- token đã cancel trước `BeginAsync()` gây `OperationCanceledException` và không
  để lại ambient root;
- RepoDb count operation bị từ chối khi streaming reader vẫn giữ operation lease
  trên cùng root.

## Concurrency

Không chạy nhiều RepoDb operation đồng thời trên cùng root connection:

```csharp
// Sai: shared root chỉ cho một database operation tại một thời điểm.
await Task.WhenAll(operation1, operation2);
```

Luôn đóng hoặc dispose reader trước command tiếp theo và trước khi complete hay
rollback root.

## Chạy integration test

```powershell
dotnet test tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests.csproj
```

Integration test dùng `WebApplicationFactory<Program>` trong environment
`Testing` và kiểm tra toàn bộ list, commit, rollback và diagnostics qua HTTP.
