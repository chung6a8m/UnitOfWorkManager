# UnitOfWork.Core

Thư viện mẫu triển khai Unit of Work trên `IDbConnection`/`IDbTransaction` với:

- ref-counting cho các lời gọi `BeginAsync()` lồng nhau;
- rollback lan truyền từ tầng trong ra transaction ngoài cùng;
- guard theo logical flow bằng `AsyncLocal`;
- fail-fast khi hai thao tác dùng chung một UoW đồng thời;
- connection/command wrapper để repository và Dapper đi qua cùng guard.

## Cơ chế ambient UnitOfWork

`UnitOfWorkManager` giữ current UoW trong một mutable holder nằm trong
`AsyncLocal`. `BeginAsync()` gắn holder trong execution context của caller trước
khi bắt đầu phần khởi tạo transaction bất đồng bộ. Cách này bảo đảm:

- caller nhìn thấy `Current` ngay sau khi `await BeginAsync()`;
- Begin lồng nhau dùng lại cùng UoW và cùng chờ transaction đang khởi tạo;
- lỗi mở connection/transaction dispose tài nguyên và xóa ambient state ở cả
  execution context của helper lẫn caller.

Public API của `IUnitOfWork` và `IUnitOfWorkManager` không phụ thuộc vào chi tiết
holder này.

## Ref-counting và rollback

Mỗi UoW bắt đầu với ref-count bằng 1. Begin lồng nhau tăng ref-count; commit hoặc
rollback ở tầng trong chỉ giảm ref-count. Transaction thật chỉ kết thúc khi tầng
ngoài cùng đưa ref-count về 0. Một rollback ở bất kỳ tầng nào đặt cờ rollback cho
toàn bộ UoW, nên commit ngoài cùng sau đó vẫn rollback transaction.

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

## Cấu trúc

```text
src/UnitOfWork.Core/
  UnitOfWork.cs
  UnitOfWorkManager.cs
  GuardedDbConnection.cs
  GuardedDbCommand.cs
  Exceptions/UnitOfWorkConcurrencyException.cs

tests/UnitOfWork.Tests/
  RefCountingTests.cs
  CommitRollbackIntegrationTests.cs
  ConcurrencyGuardTests.cs
  AsyncFlowIsolationTests.cs
  DisposalTests.cs
  UnitOfWorkManagerAmbientTests.cs
  SqliteTestDbTests.cs
```

## Chạy build và test

Yêu cầu .NET 8 SDK trở lên:

```powershell
dotnet restore UnitOfWork.slnx
dotnet build UnitOfWork.slnx --no-restore --warnaserror
dotnet test UnitOfWork.slnx --no-restore
```

Test project tắt xUnit parallelization vì ambient state là static và được phân
tách theo execution context. `UnitOfWorkTestBase` luôn reset state này sau mỗi
test, kể cả khi test thất bại.
