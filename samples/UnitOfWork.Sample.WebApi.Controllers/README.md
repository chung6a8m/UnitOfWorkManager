# UnitOfWork.Sample.WebApi.Controllers

Sample .NET 8 ASP.NET Core Web API dùng **MVC Controllers + Dapper + SQLite**
để minh họa `UnitOfWork.Core` trong cấu trúc gần ứng dụng doanh nghiệp.

## Kiến trúc

```text
HTTP Controller
    -> CounterApplicationService
        -> NestedCounterService
        -> ICounterRepository
            -> DapperCounterRepository
                -> transaction-bound DbConnection
                    -> SQLite
```

`UnitOfWorkManager` được đăng ký singleton. Ambient root được lưu bằng
`AsyncLocal`, vì vậy mỗi request/execution flow có root riêng, trong khi các
service lồng nhau của cùng request vẫn dùng chung transaction và repository
cache.

Repository chỉ nhận `DbConnection` do Unit of Work cung cấp. Dapper không nhận
tham số `transaction`, và code không tự gán `command.Transaction`; command tạo
từ connection facade đã tự enlist vào root transaction.

## Chạy sample

```powershell
dotnet run --project samples/UnitOfWork.Sample.WebApi.Controllers/UnitOfWork.Sample.WebApi.Controllers.csproj
```

Mặc định ASP.NET Core Development dùng địa chỉ được in ra console. File
`UnitOfWork.Sample.WebApi.Controllers.http` giả định `http://localhost:5000`;
hãy sửa biến `@host` nếu ứng dụng chạy ở cổng khác.

Database SQLite được tạo bằng tên ngẫu nhiên trong `Path.GetTempPath()`, dùng
`Pooling=False`, tồn tại trong suốt vòng đời host và được xóa khi host dừng.

## Endpoints

### Đọc dữ liệu đã commit

```http
GET /api/counters
```

Kết quả ban đầu:

```json
[]
```

### Commit outer + nested service

```http
POST /api/counters/commit
Content-Type: application/json

{
  "outerValue": 10,
  "innerValue": 20
}
```

Outer service insert `10`; nested service mở scope riêng, insert `20` và gọi
`CompleteAsync()`. Hai scope là hai lease trên cùng root transaction. Root chỉ
commit sau khi outer scope complete.

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

### Rollback từ inner scope incomplete

```http
POST /api/counters/rollback
Content-Type: application/json

{
  "outerValue": 30,
  "innerValue": 40
}
```

Nested service insert `40` nhưng không gọi `CompleteAsync()`. Khi inner scope
được dispose, root nhận rollback request. Vì vậy cả `30` và `40` bị rollback dù
outer scope vẫn gọi `CompleteAsync()`.

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

Endpoint kiểm tra:

- hai lần `GetRepository<ICounterRepository>()` trong cùng root trả cùng instance;
- command nhận timeout `15` giây từ `UnitOfWorkOptions`;
- `BeginAsync()` quan sát token đã cancel và không để lại ambient root;
- Dapper command thứ hai bị từ chối khi streaming reader còn giữ operation lease.

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

## Cancellation

Controller nhận `CancellationToken` của request và chuyển nguyên token qua
application service, nested service, `BeginAsync()`, `CompleteAsync()` và Dapper
`CommandDefinition`. Sample không bắt request cancellation tại controller;
client hủy request sẽ làm cancellation truyền xuyên suốt stack.

## Quy tắc concurrency

Không chạy `Task.WhenAll` cho nhiều Dapper command trên cùng root connection.
Shared root chỉ cho một database operation tại một thời điểm. Reader streaming
phải được đóng/dispose trước command tiếp theo hoặc trước khi complete/rollback.

## Chạy integration test

```powershell
dotnet test tests/UnitOfWork.Sample.WebApi.Tests/UnitOfWork.Sample.WebApi.Tests.csproj
```

Test dùng `WebApplicationFactory<Program>` để gọi đủ bốn endpoint và xác nhận:

- database ban đầu rỗng;
- commit giữ lại `10`, `20`;
- rollback không giữ lại `30`, `40`;
- diagnostics trả `allPassed: true`.
