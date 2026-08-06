# UnitOfWork.Core — Bộ test

Bộ test cho `UnitOfWork`/`UnitOfWorkManager` bản ref-counting kèm 2 lớp guard
(fail-fast theo flow AsyncLocal, và fail-fast khi 2 thao tác chạy đồng thời).

## Đã sửa (lần 2): vẫn còn lỗi khoá file trên Windows sau lần sửa đầu

Sau khi thêm đủ `Dispose()`, lỗi vẫn xảy ra — nhưng lần này CHỈ ở các test dùng
`UnitOfWorkManager` (và 1 test dùng `SuppressFlow` lồng UoW). Hai nguyên nhân thật sự:

1. **WAL mode tạo file phụ `-wal`/`-shm` bị Windows giữ khoá.** Bản sửa lần 1 bật
   `PRAGMA journal_mode=WAL;` để 1 connection khác đọc được dữ liệu trong lúc transaction
   ghi khác còn mở (dùng trong `Commit_Only_Persists_When_Outermost_Call_Completes`). Nhưng
   thực ra **không cần WAL** cho việc này: ở chế độ rollback-journal MẶC ĐỊNH của SQLite,
   RESERVED lock (writer đang có transaction dở) không chặn reader khác — reader chỉ đọc
   snapshot trước khi commit, không bị `SQLITE_BUSY`. WAL bị bỏ hẳn — loại luôn nguồn phát
   sinh file `-wal`/`-shm` vốn là thứ hay bị Windows (đặc biệt AV/indexer) giữ khoá một nhịp
   sau khi connection đã `Dispose()`.

2. **`UnitOfWorkManager._current` là `AsyncLocal` *tĩnh*** — dùng chung cho MỌI instance
   `UnitOfWorkManager` trong cả process test. Nếu 1 test fail giữa chừng (throw trước khi kịp
   chạy `ClearCurrent()`), giá trị cũ có thể "rò" sang test kế tiếp — nhất là khi
   `[assembly: CollectionBehavior(DisableTestParallelization = true)]` khiến các test chạy
   tuần tự và (tuỳ cách xUnit lập lịch) có thể không luôn tạo flow async hoàn toàn độc lập.
   Test sau đó vô tình "kế thừa" `UnitOfWork` đã đóng của test trước, thao tác nhầm connection,
   gây lỗi khó đoán (kể cả các trường hợp chạy rất lâu — 30+ giây — do `busy_timeout` chờ một
   connection thực chất đã chết).

   Fix: thêm `UnitOfWorkTestBase` (`Fixtures/UnitOfWorkTestBase.cs`) — mọi test class giờ kế
   thừa lớp này. xUnit tạo 1 instance MỚI cho mỗi `[Fact]` và LUÔN gọi `Dispose()` sau khi test
   chạy xong (dù pass hay fail) — `Dispose()` ở đây dọn sạch cả 2 AsyncLocal tĩnh
   (`UnitOfWork.AmbientFlowId` và `UnitOfWorkManager`'s current-UoW tracker qua
   `UnitOfWorkManager.ResetAmbientStateForTests()`, một method `internal` chỉ để test dùng).
   Nhờ đó trạng thái luôn sạch giữa các test bất kể test trước có tự dọn đúng hay không.

## Đã sửa (lần 1): lỗi Windows "process cannot access the file...db" khi chạy `dotnet test`

Nguyên nhân: một số test tạo `UnitOfWork` trực tiếp (không qua `UnitOfWorkManager`) chỉ gọi
`RollbackAsync()`/`CommitAsync()` mà quên gọi `Dispose()` — trên Linux, xoá file đang còn
handle mở vẫn được phép nên không lộ ra; trên Windows, `File.Delete()` sẽ ném `IOException`
vì file đang bị khoá bởi chính connection chưa đóng đó.

Hai thay đổi:

1. **Bổ sung `uow.Dispose()`** ở mọi test tạo `UnitOfWork` trực tiếp
   (`ConcurrencyGuardTests`, `AsyncFlowIsolationTests`, `RefCountingTests`) — những chỗ trước
   đó chỉ gọi `RollbackAsync()`/`CommitAsync()` rồi bỏ ngỏ.
2. **`SqliteTestDb`** giờ mở connection với `Pooling=False` (để `Dispose()` đóng handle file
   thật ngay, không giữ trong pool nội bộ chờ tái sử dụng — hành vi pooling mặc định là một
   nguồn phổ biến khác gây khoá file trên Windows), và `Dispose()` có retry ngắn (5 lần, cách
   nhau 100ms) khi xoá file — phòng trường hợp AV/indexer Windows giữ handle tạm thời. Nếu sau
   5 lần vẫn lỗi, đó là dấu hiệu THẬT của leak — test nên fail để lộ ra, không nuốt lỗi.

## Cấu trúc

```
src/UnitOfWork.Core/          # code chính (đúng bản đã chốt trong cuộc trò chuyện)
  UnitOfWork.cs                - ref-counting + EnsureSameLogicalFlow + GuardedExecuteAsync
  GuardedDbConnection.cs       - bọc IDbConnection để Repository/Dapper cũng đi qua guard
  GuardedDbCommand.cs
  UnitOfWorkManager.cs
  Exceptions/UnitOfWorkConcurrencyException.cs

tests/UnitOfWork.Tests/       # xUnit + Microsoft.Data.Sqlite (transaction THẬT, không mock)
  Fixtures/SqliteTestDb.cs     - tạo file SQLite tạm cho mỗi test, WAL mode để đọc song song an toàn
  Fixtures/CounterRepository.cs- repository mẫu chạy qua GuardedDbConnection
  RefCountingTests.cs           - BeginAsync lồng nhau, Commit chỉ chạy thật ở outermost, rollback lan lên trên
  ConcurrencyGuardTests.cs      - 2 thao tác chạy đồng thời trên cùng UoW -> UnitOfWorkConcurrencyException
  AsyncFlowIsolationTests.cs    - Task.Run mặc định vẫn thấy flow cha; SuppressFlow thì KHÔNG; dùng nhầm UoW của flow khác -> exception
  DisposalTests.cs              - Dispose idempotent, clear AsyncLocal, ClearCurrent cho phép Begin lại
  CommitRollbackIntegrationTests.cs - end-to-end qua UnitOfWorkManager, xác nhận dữ liệu thật trong SQLite
```

## Vì sao dùng SQLite thật thay vì mock `IDbConnection`?

`IDbConnection`/`IDbTransaction` có nhiều thành viên phụ (state, isolation level...) nên
mock bằng Moq dễ che giấu bug thật. Dùng SQLite file-based (WAL mode) cho phép:

- Kiểm chứng **transaction thật sự commit hay rollback** (đọc lại bằng connection độc lập).
- Test guard đồng thời chạy qua đúng đường `GuardedDbConnection → GuardedDbCommand → ExecuteNonQuery`
  giống hệt luồng Dapper thật sẽ đi qua, không chỉ test logic nội bộ.

## Chạy test

Môi trường tạo bộ test này **không có sẵn .NET SDK / không có mạng** để restore NuGet,
nên chưa build/run được tại đây. Trên máy có .NET 8 SDK:

```bash
cd uow-solution
dotnet new sln -n UnitOfWork
dotnet sln add src/UnitOfWork.Core/UnitOfWork.Core.csproj
dotnet sln add tests/UnitOfWork.Tests/UnitOfWork.Tests.csproj

dotnet test
```

## Lưu ý quan trọng khi đọc test

- `UnitOfWork.AmbientFlowId` và `UnitOfWorkManager`'s `_current` đều là `AsyncLocal` **tĩnh**
  (dùng chung toàn process, đúng như thiết kế thật). Vì vậy file `AssemblyInfo.cs` trong
  project test tắt hẳn song song hóa của xUnit
  (`[assembly: CollectionBehavior(DisableTestParallelization = true)]`) — nếu không, các test
  class chạy song song trên nhiều thread có thể "lẫn" AsyncLocal của nhau và ra kết quả flaky.
- `ConcurrencyGuardTests` không dùng SQLite chậm/`Thread.Sleep` để giả lập race — vì
  `GuardedDbCommand` release guard ngay khi `ExecuteNonQuery()` trả về, một `Thread.Sleep` đặt
  *sau* lệnh insert sẽ không còn giữ guard nữa. Thay vào đó test gọi thẳng
  `UnitOfWork.RunGuardedAsync` (internal, mở qua `InternalsVisibleTo`) với `Task.Delay` bên
  trong để tạo cửa sổ thời gian tất định cho request thứ hai va vào guard.
- `RefCountingTests.Commit_Only_Persists_When_Outermost_Call_Completes` là test quan trọng
  nhất về mặt ngữ nghĩa: xác nhận `CommitAsync()` ở tầng lồng bên trong **không** làm dữ liệu
  xuất hiện trong DB — chỉ tầng ngoài cùng (refcount về 0) mới thực sự `Transaction.Commit()`.

## Những gì bộ test CHƯA cover (gợi ý mở rộng)

- Test cho `TransactionBehavior` (MediatR) — cần thêm package `MediatR` nếu bạn muốn test
  riêng pipeline behavior thay vì gọi `UnitOfWorkManager` trực tiếp như ở đây.
- Test cho các database khác ngoài SQLite (SQL Server/PostgreSQL) — vốn không áp dụng vì bản
  hiện tại đã bỏ savepoint/nested transaction thật, chỉ còn ref-counting trên 1 transaction.
- Load test/stress test cho `Interlocked` guard dưới tải cao (hàng trăm task đua nhau) — bộ
  test hiện tại chỉ chứng minh guard *hoạt động đúng*, chưa đo hiệu năng.
