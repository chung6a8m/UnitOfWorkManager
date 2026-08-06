# UnitOfWork.Core

Thư viện mẫu triển khai Unit of Work trên `IDbConnection`/`IDbTransaction`.

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
  Dapper không được gán `command.Transaction` thủ công.
- Ambient root được cô lập theo từng `UnitOfWorkManager`; hai manager không chia
  sẻ current root, transaction, hay repository cache.

## Chính sách execution flow và concurrency

`AsyncLocal` truyền current root vào child task kế thừa execution context. Những
task đó có thể dùng root tuần tự khi root còn active, nhưng không thể chạy hai
command dùng chung root cùng lúc: operation lease sẽ reject overlap ngay lập tức.
`ExecutionContext.SuppressFlow()` không nhận current root và bị reject; scope
giữ lại sau khi root finalize cũng bị reject theo lifecycle state. Thư viện không
cấm mọi `Task.Run`; nó cấm shared operation song song và mọi usage ngoài active
lifecycle.

Repository cache được tạo đúng một lần dưới root lifecycle lock. Repository
factory là constructor-only, đồng bộ và không làm I/O/`await`; nếu factory ném
lỗi, instance/key chưa được cache và lần gọi tiếp theo có thể thử lại. Lock order
luôn là `scope settlement lock -> root lifecycle lock -> operation flag`.

Mẫu dùng cơ bản:

```csharp
await using var scope = await manager.BeginAsync();

// Thực hiện repository/Dapper work qua scope hoặc manager.Current.

await scope.CompleteAsync();
```

## Migration từ API cũ

| Before | After |
|---|---|
| `IUnitOfWork` | `IUnitOfWorkScope` / `IUnitOfWorkContext` |
| `CommitAsync()` | `CompleteAsync()` |
| dispose + `ClearCurrent()` | `await using var scope = await BeginAsync()` |
| factory `(type, connection, transaction)` | `(type, connection)` |
| `command.Transaction = transaction` | xóa assignment |
| nested begin trả cùng owner | nested begin trả distinct lease |

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
  IUnitOfWorkContext.cs
  IUnitOfWorkScope.cs
  RootUnitOfWork.cs
  UnitOfWorkManager.cs
  UnitOfWorkScope.cs

tests/UnitOfWork.Tests/
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
