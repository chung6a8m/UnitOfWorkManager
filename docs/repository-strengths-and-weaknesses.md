# Phân tích điểm mạnh và điểm yếu của UnitOfWorkManager

## 1. Phạm vi đánh giá

- Repository: `chung6a8m/UnitOfWorkManager`
- Nhánh: `main`
- Commit được phân tích: `0703fea9afa06f0e1a314e6fb7ea0f8656f925cc`
- Ngày đánh giá: 2026-08-06
- Phạm vi: kiến trúc, public API, transaction semantics, ambient state, concurrency guard, resource lifecycle, khả năng tích hợp ADO.NET/ORM và chất lượng kiểm thử.

Đánh giá này dựa trên toàn bộ mã nguồn và test hiện có trong repository. Môi trường phân tích không có .NET SDK nên không chạy lại `dotnet build`/`dotnet test`; các nhận xét về hành vi được suy ra từ mã nguồn và các test đã viết.

## 2. Kết luận tổng quan

Repository có nền tảng ý tưởng tốt cho một thư viện Unit of Work nhỏ, dễ đọc và có mục tiêu rõ ràng:

- dùng một transaction thật cho các lời gọi lồng nhau;
- rollback ở tầng trong lan ra tầng ngoài;
- ambient Unit of Work đi theo `AsyncLocal`;
- phát hiện sớm thao tác đồng thời trên cùng connection/transaction;
- kiểm thử bằng SQLite thật thay vì chỉ mock.

Điểm nổi bật nhất là tác giả đã xử lý khá kỹ các tình huống khó quanh việc publish ambient state trong lúc transaction đang khởi tạo, cleanup khi initialization lỗi và rò rỉ tài nguyên trên Windows.

Tuy nhiên, thiết kế hiện tại vẫn phù hợp hơn với **proof of concept hoặc thư viện nội bộ có quy ước sử dụng nghiêm ngặt** hơn là một abstraction an toàn để đưa thẳng vào production. Một số invariant quan trọng chỉ được kỳ vọng ở repository code chứ chưa được public API cưỡng chế, đặc biệt:

1. `IUnitOfWork.Connection` trả về connection thật nên có thể đi vòng qua toàn bộ guard.
2. Command tạo từ `GuardedDbConnection` không tự gắn transaction; repository phải nhớ gắn thủ công.
3. Nested `BeginAsync()` trả về cùng một object có quyền `Dispose`, `Commit` và `Rollback`, nên tầng trong có thể phá vòng đời của tầng ngoài.
4. Không có state machine ngăn double commit, double rollback, ref-count âm hoặc cleanup sai thứ tự.
5. Các method mang tên `Async` vẫn dùng transaction operation đồng bộ và wrapper không cung cấp đường ADO.NET async thực sự.
6. Guard của `ExecuteReader()` kết thúc trước khi reader được đọc hết, nên không bảo vệ toàn bộ thời gian sử dụng resource.

### Đánh giá định tính

| Tiêu chí | Mức đánh giá | Nhận xét |
|---|---:|---|
| Độ rõ ràng của ý tưởng | Tốt | Mục tiêu nhỏ, dễ hiểu, README mô tả đúng phần lớn semantics chính. |
| Nested transaction semantics | Tốt | Ref-counting và rollback propagation được thể hiện rõ và có integration test. |
| Xử lý ambient initialization | Rất tốt | Mutable holder và shared initialization task xử lý được reentrant/nested begin trong lúc mở connection. |
| Chất lượng test hiện tại | Khá tốt | Có SQLite thật, controlled failures, cleanup tài nguyên và test flow isolation. |
| An toàn của public API | Yếu | Nhiều đường bypass guard/transaction và quyền sở hữu resource chưa được giới hạn. |
| Async fidelity | Yếu | Tên API async nhưng phần lớn database operation vẫn đồng bộ. |
| Khả năng dùng như package production | Chưa đủ | Thiếu CI, package metadata, license, provider matrix và các test invariant quan trọng. |

## 3. Kiến trúc hiện tại

Luồng chính của repository:

1. `UnitOfWorkManager.BeginAsync()` kiểm tra ambient holder trong `AsyncLocal`.
2. Nếu đã có Unit of Work, manager tăng ref-count và trả lại cùng instance.
3. Nếu chưa có, manager tạo connection, tạo `UnitOfWork`, publish holder trước, sau đó khởi tạo transaction bất đồng bộ.
4. `UnitOfWork.GetRepository<T>()` tạo repository theo factory và cache theo type.
5. Repository nhận `GuardedDbConnection` cùng transaction hiện tại.
6. Command tạo từ guarded connection được bọc trong `GuardedDbCommand` để kiểm tra flow và chặn concurrent operation.
7. `CommitAsync()`/`RollbackAsync()` giảm ref-count; transaction thật chỉ kết thúc khi ref-count về 0.
8. Caller tự `Dispose()` Unit of Work và gọi `UnitOfWorkManager.ClearCurrent()`.

Các file chính:

- `src/UnitOfWork.Core/UnitOfWork.cs`
- `src/UnitOfWork.Core/UnitOfWorkManager.cs`
- `src/UnitOfWork.Core/GuardedDbConnection.cs`
- `src/UnitOfWork.Core/GuardedDbCommand.cs`
- `src/UnitOfWork.Core/IUnitOfWork.cs`
- `src/UnitOfWork.Core/IUnitOfWorkManager.cs`

## 4. Điểm mạnh

### 4.1. Thiết kế lõi nhỏ, tập trung và ít phụ thuộc

Core chỉ dựa trên `IDbConnection`/`IDbTransaction`, không phụ thuộc EF Core, Dapper hoặc một DI container cụ thể. Điều này giúp ý tưởng có thể áp dụng cho nhiều provider và nhiều kiểu repository.

**Ví dụ cụ thể**

`UnitOfWorkManager` chỉ cần:

- `IDbConnectionFactory` để tạo connection;
- repository factory nhận `Type`, connection và transaction.

**So sánh**

- Một Unit of Work bọc trực tiếp `DbContext` sẽ tích hợp EF Core tự nhiên hơn nhưng bị coupling với EF Core.
- Thiết kế hiện tại trung lập hơn, đổi lại phải tự xây các invariant mà `DbContext` đã cung cấp, chẳng hạn tracking, transaction enlistment, async APIs và lifecycle management.

### 4.2. Ref-counting cho nested service call đơn giản và dễ dự đoán

`BeginAsync()` lồng nhau trong cùng ambient flow dùng lại cùng một Unit of Work. Commit ở tầng trong chỉ giảm ref-count; commit thật chỉ xảy ra ở tầng ngoài cùng.

**Ví dụ hiện có**

Trong `CommitRollbackIntegrationTests.Simulated_Nested_Service_Calls_Share_One_Real_Transaction`:

- outer service insert một dòng;
- inner service tự gọi `BeginAsync()` và insert một dòng;
- inner `CommitAsync()` chưa commit transaction thật;
- outer `CommitAsync()` mới commit cả hai dòng.

**So sánh với cách triển khai ngây thơ**

Một manager tạo transaction mới ở mỗi service có thể dẫn tới:

- inner commit thành công nhưng outer rollback không hoàn tác được inner;
- nhiều connection và transaction không cần thiết;
- khó đảm bảo tính nguyên tử của một use case xuyên nhiều service.

Thiết kế hiện tại tránh được ba vấn đề đó khi yêu cầu nghiệp vụ là “toàn bộ call chain cùng thành công hoặc cùng thất bại”.

### 4.3. Rollback propagation phù hợp với atomic business operation

Một rollback ở bất kỳ tầng nào đặt `_rollbackRequested = true`. Sau đó outer caller dù gọi `CommitAsync()` thì transaction thật vẫn rollback.

**Ví dụ hiện có**

`RefCountingTests.Inner_Rollback_Forces_Outer_To_Rollback_Everything` xác nhận dữ liệu outer và inner đều không được lưu.

**So sánh với savepoint**

- Thiết kế hiện tại: inner rollback làm thất bại toàn bộ Unit of Work.
- Thiết kế dùng savepoint: inner rollback có thể chỉ hoàn tác phần inner và outer vẫn tiếp tục.

Cách hiện tại đơn giản và an toàn cho use case nguyên tử. Nó không phù hợp nếu nghiệp vụ cần partial recovery, nhưng repository đã mô tả rõ là không dùng savepoint.

### 4.4. Xử lý ambient state trong giai đoạn initialization được suy nghĩ kỹ

`UnitOfWorkManager` publish một mutable holder vào `AsyncLocal` trước khi `OpenAsync()`/`BeginTransaction()` hoàn tất. Holder chứa cả Unit of Work và shared initialization task.

Điểm này giải quyết được những tình huống mà cách triển khai đơn giản thường sai:

- hai `BeginAsync()` xảy ra gần nhau chỉ tạo một connection;
- nested/reentrant begin trong lúc connection đang mở cùng chờ một initialization task;
- caller thấy cùng một ambient object thay vì tạo Unit of Work thứ hai;
- initialization lỗi thì holder được làm sạch và connection được dispose.

**So sánh với cách publish sau `await`**

Nếu chỉ gán `AsyncLocal` sau khi transaction đã mở xong, một lời gọi `BeginAsync()` thứ hai trong khoảng chờ có thể tạo thêm connection và transaction. Thiết kế hiện tại tránh race này.

### 4.5. Bảo toàn lỗi initialization khi cleanup cũng lỗi

Trong `InitializeAsync`, nếu mở transaction lỗi và `Dispose()` tiếp tục lỗi, exception initialization vẫn là exception chính; cleanup error được lưu trong `Exception.Data` với key `UnitOfWorkCleanupException`.

Đây là lựa chọn hợp lý vì lỗi gốc thường quan trọng hơn lỗi phát sinh khi dọn dẹp.

**So sánh**

- Cách đơn giản: cleanup exception ghi đè initialization exception, làm mất nguyên nhân ban đầu.
- Cách hiện tại: giữ nguyên primary failure nhưng vẫn không nuốt cleanup failure.

Test `Initialization_Error_Is_Preserved_When_Cleanup_Also_Fails` kiểm chứng trực tiếp hành vi này.

### 4.6. Fail-fast concurrency guard tốt hơn việc để provider lỗi ngẫu nhiên

`Interlocked.CompareExchange` bảo đảm chỉ một guarded operation được chạy tại một thời điểm trên cùng Unit of Work. Khi vi phạm, code ném `UnitOfWorkConcurrencyException` với thông báo giàu ngữ cảnh.

**Ví dụ**

Nếu hai nhánh `Task.WhenAll` cùng dùng một repository trong cùng Unit of Work, thao tác thứ hai bị chặn ngay thay vì để `IDbConnection`/`IDbTransaction` rơi vào trạng thái không xác định.

**So sánh**

- Không có guard: lỗi có thể xuất hiện sâu trong provider, khác nhau theo database và khó tái hiện.
- Guard hiện tại: lỗi xuất hiện tại boundary của Unit of Work và hướng người dùng kiểm tra parallel execution.

### 4.7. Test bằng SQLite thật tạo độ tin cậy cao hơn mock thuần túy

Bộ test không chỉ kiểm tra số lần gọi `Commit()`/`Rollback()` mà đọc dữ liệu lại bằng connection độc lập để xác nhận transaction thật.

Các điểm tốt trong test infrastructure:

- file database ngẫu nhiên trong temp;
- `Pooling=False` để phát hiện resource leak trên Windows;
- cleanup cả file chính và sidecar;
- retry ngắn khi xóa file để giảm nhiễu từ antivirus/indexer;
- database riêng cho các logical flow để không nhầm `SQLITE_BUSY` với lỗi flow guard;
- `ControlledDbConnection` để kiểm tra lỗi initialization/disposal có tính tất định;
- test base reset static ambient state kể cả khi test thất bại.

**So sánh với mock**

Mock `IDbTransaction.Commit()` có thể xác nhận method được gọi nhưng không chứng minh command thực sự nằm trong transaction. SQLite integration test có thể bắt được nhiều lỗi wiring mà mock bỏ sót.

### 4.8. Cleanup cố gắng giải phóng cả transaction lẫn connection

`Dispose()` không dừng ngay nếu transaction dispose lỗi. Nó vẫn tiếp tục dispose connection và có thể trả `AggregateException` nếu cả hai cùng lỗi.

Đây là cách xử lý tốt hơn `try/finally` đơn giản chỉ làm lộ lỗi đầu tiên và bỏ qua resource còn lại.

### 4.9. Repository cache theo type giảm việc tạo lại object

`GetRepository<T>()` cache repository trong một Unit of Work. Điều này phù hợp khi repository giữ connection/transaction và không nên được tạo lại cho mỗi method call.

### 4.10. Quy ước encoding và line ending rõ ràng

`.editorconfig` và `.gitattributes` cho thấy repository chú ý đến môi trường Windows, UTF-8 BOM cho C# và CRLF cho phần lớn file. Điều này có lợi cho code có nội dung tiếng Việt và giảm thay đổi line ending không cần thiết.

## 5. Điểm yếu và rủi ro

## 5.1. Mức ưu tiên P0 — có thể phá transaction correctness

### 5.1.1. Public API trả connection thật và cho phép đi vòng qua guard

`IUnitOfWork.Connection` trả về `IDbConnection` gốc. `GuardedDbConnection` chỉ được truyền vào repository factory.

Do đó caller có thể viết:

```csharp
var uow = await manager.BeginAsync();
using var command = uow.Connection.CreateCommand();
command.CommandText = "UPDATE Accounts SET Balance = Balance - 100";
command.ExecuteNonQuery();
```

Command này:

- không đi qua `GuardedDbCommand`;
- không chạy `EnsureSameLogicalFlow()`;
- không chạy concurrent-operation guard;
- không tự thuộc transaction của Unit of Work.

Đây là lỗ hổng lớn nhất vì public API cho phép phá chính invariant mà thư viện quảng bá.

**So sánh với thiết kế an toàn hơn**

- Không public raw connection; hoặc
- `IUnitOfWork.Connection` phải là connection facade đã guard và bind transaction; hoặc
- chỉ expose method thực thi qua Unit of Work, không expose connection ownership trực tiếp.

### 5.1.2. Command không tự gắn transaction

`GuardedDbConnection.CreateCommand()` chỉ bọc command, không gán:

```csharp
innerCommand.Transaction = owner.Transaction;
```

Trong test, `CounterRepository.Insert()` phải tự làm:

```csharp
cmd.Transaction = _transaction;
```

Nếu một repository quên dòng này, command vẫn chạy qua concurrency guard nhưng chạy ngoài transaction.

**Ví dụ hậu quả**

1. Repository A insert và nhớ gắn transaction.
2. Repository B update nhưng quên gắn transaction.
3. Business operation rollback.
4. Insert của A bị hoàn tác, update của B có thể đã auto-commit và vẫn tồn tại.

Kết quả là Unit of Work mất tính nguyên tử dù mọi code đều lấy repository qua `GetRepository<T>()`.

**So sánh với wrapper production-grade**

Connection facade nên tự gắn transaction hiện tại cho mọi command và từ chối transaction khác, trừ khi API chủ động cho phép override.

### 5.1.3. Nested scope trả cùng một disposable object

Mỗi `BeginAsync()` lồng nhau trả cùng instance `UnitOfWork`. Điều này làm ref-counting đơn giản, nhưng mỗi tầng đều nhận toàn quyền:

- `CommitAsync()`;
- `RollbackAsync()`;
- `Dispose()`;
- truy cập connection/transaction.

**Ví dụ lỗi rất dễ xảy ra**

```csharp
async Task InnerAsync(IUnitOfWorkManager manager)
{
    using var uow = await manager.BeginAsync();
    // xử lý nghiệp vụ
    await uow.CommitAsync();
}
```

`using` ở inner service sẽ dispose connection và transaction đang thuộc outer service. Outer tiếp tục chạy sẽ nhận `ObjectDisposedException`.

Bộ test hiện tại tránh lỗi này bằng quy ước “chỉ outer dispose”, nhưng API không thể hiện hoặc cưỡng chế quy ước đó.

**So sánh với lease/scope token**

Một thiết kế an toàn hơn trả object scope riêng cho mỗi `BeginAsync()`:

- inner scope chỉ có quyền `CompleteAsync()` hoặc đánh dấu rollback;
- chỉ root Unit of Work sở hữu connection/transaction;
- dispose inner scope chỉ giảm ref-count, không dispose root resource.

### 5.1.4. Thiếu lifecycle state machine

State hiện tại chỉ gồm các field rời rạc:

- `_refCount`;
- `_rollbackRequested`;
- `_isDisposed`;
- `_operationInProgress`.

Không có trạng thái rõ ràng như `Active`, `Committed`, `RolledBack`, `Faulted`, `Disposed`.

**Các tình huống chưa được ngăn**

- gọi `CommitAsync()` hai lần ở single-level scope;
- gọi `RollbackAsync()` sau khi đã commit;
- ref-count giảm xuống số âm;
- commit lỗi sau khi ref-count đã về 0;
- retry commit trên transaction có outcome không rõ;
- `Dispose()` trong lúc một operation đang chạy;
- gọi `ClearCurrent()` khi transaction vẫn active.

Ví dụ, `CommitAsync()` dùng `Interlocked.Decrement(ref _refCount)` trước khi commit. Nếu `Transaction.Commit()` ném exception, ref-count đã là 0. Lần gọi tiếp theo làm ref-count thành -1 và code vẫn có thể thử commit lại.

Một state machine sẽ giúp fail-fast với exception rõ ràng và giữ invariant nhất quán.

### 5.1.5. `ClearCurrent()` có thể làm orphan transaction

`ClearCurrent()` public và chỉ xóa ambient holder. Nó không:

- kiểm tra Unit of Work còn active hay không;
- rollback;
- dispose;
- đánh dấu fault.

Caller có thể làm:

```csharp
var first = await manager.BeginAsync();
manager.ClearCurrent();
var second = await manager.BeginAsync();
```

Khi đó hai Unit of Work có thể cùng tồn tại trong một logical flow, còn transaction đầu bị mất khỏi manager nhưng chưa được kết thúc.

Public API cleanup nên gộp việc clear ambient với dispose root scope thay vì để caller tự ghép nhiều bước.

### 5.1.6. Ambient Unit of Work là static và dùng chung giữa mọi manager instance

`UnitOfWorkManager._current` là `static AsyncLocal`. Vì vậy trong cùng execution context:

```csharp
var a = await managerForDatabaseA.BeginAsync();
var b = await managerForDatabaseB.BeginAsync();
```

`managerForDatabaseB` có thể thấy Unit of Work của manager A và dùng lại nó, dù hai manager có:

- connection factory khác nhau;
- database khác nhau;
- repository factory khác nhau;
- tenant khác nhau.

Đây là rủi ro lớn cho ứng dụng multi-database hoặc multi-tenant.

**So sánh với thiết kế an toàn hơn**

Ambient state cần được key theo manager/context identity, ví dụ:

- `AsyncLocal<Dictionary<Guid, Holder>>`;
- holder không static trên type hoặc có context key riêng;
- một ambient context service scoped theo DI.

## 5.2. Mức ưu tiên P1 — guard và async semantics chưa hoàn chỉnh

### 5.2.1. Guard không giữ suốt vòng đời của data reader

`GuardedDbCommand.ExecuteReader()` chạy `_inner.ExecuteReader()` trong guard rồi trả `IDataReader`. Guard được release ngay khi method trả về, trong khi caller vẫn đang đọc rows.

**Ví dụ**

```csharp
using var reader = command.ExecuteReader();
while (reader.Read())
{
    // Trong thời gian này guard đã được thả.
}
```

Một command thứ hai có thể bắt đầu trong khi reader đầu vẫn giữ connection hoặc server cursor.

Rủi ro này rõ hơn với streaming/deferred enumeration, nơi reader sống lâu hơn lời gọi `ExecuteReader()`.

**Thiết kế tốt hơn**

Trả một `GuardedDataReader` giữ operation lease và chỉ release guard khi reader được `Dispose`/`Close`.

### 5.2.2. API async nhưng database operation chủ yếu vẫn đồng bộ

Các dấu hiệu:

- `BeginTransactionAsync()` gọi `Connection.BeginTransaction()` đồng bộ;
- `CommitAsync()` gọi `Transaction.Commit()` đồng bộ;
- `RollbackAsync()` gọi `Transaction.Rollback()` đồng bộ;
- `GuardedDbCommand` chỉ implement `IDbCommand` sync;
- sync operation được bọc bằng `Task.FromResult` hoặc async lambda không có I/O async.

Hệ quả:

- thread request vẫn bị block khi provider thực hiện commit/rollback/command;
- không hỗ trợ `CancellationToken`;
- không tận dụng `DbConnection.BeginTransactionAsync`, `DbTransaction.CommitAsync`, `DbCommand.ExecuteReaderAsync`;
- các thư viện cần `DbCommand`/`DbConnection` async thực sự không thể đi qua wrapper hiện tại một cách đầy đủ.

Tên method async hiện tạo kỳ vọng mạnh hơn khả năng thật của implementation.

### 5.2.3. Guarded connection vẫn cho repository quản lý resource thuộc Unit of Work

`GuardedDbConnection` forward trực tiếp:

- `Dispose()`;
- `Close()`;
- `Open()`;
- `BeginTransaction()`;
- `ChangeDatabase()`;
- setter `ConnectionString`.

Repository có thể vô tình:

- dispose connection của root Unit of Work;
- close connection giữa operation;
- tạo transaction cạnh tranh;
- đổi database hoặc connection string;
- phá transaction đang active.

Một facade nên hạn chế ownership operation, ví dụ:

- `Dispose()` no-op hoặc chỉ dispose wrapper;
- chặn `Close`, `Open`, `BeginTransaction`, `ChangeDatabase`;
- connection string chỉ đọc;
- command luôn bind transaction root.

### 5.2.4. Flow guard không đồng nghĩa với task ownership tuyệt đối

`AsyncLocal` mặc định chảy vào `Task.Run`. Test `Task_Run_Without_SuppressFlow_Can_Still_See_Same_Flow_And_Succeeds` xác nhận child task tuần tự được dùng cùng Unit of Work.

Điều này hợp lý nếu child task thật sự thuộc cùng logical operation, nhưng cũng có nghĩa:

- truyền reference vào background task không nhất thiết bị chặn;
- nếu background task chạy sau khi request kết thúc, lỗi chỉ xuất hiện do disposed state;
- hai child task tuần tự vẫn được phép dùng cùng Unit of Work;
- chỉ trường hợp thao tác thực sự overlap mới bị operation guard chặn.

Vì vậy thông điệp “không share Unit of Work qua Task.Run” chưa được bảo đảm chỉ bằng owner flow ID. Cần tài liệu chính xác hoặc scope token/cancellation gắn với request lifecycle.

### 5.2.5. Repository cache chưa được bảo vệ khỏi concurrent creation

`_repositories` là `Dictionary<Type, object>` và `GetRepository<T>()` không đi qua `_operationInProgress` guard.

Do `AsyncLocal` chảy vào child tasks, hai task có thể đồng thời gọi `GetRepository<T>()` trước khi chạy database command. Khi đó có thể:

- tạo repository hai lần;
- ghi concurrent vào `Dictionary` không thread-safe;
- quan sát object không nhất quán.

Có thể sửa bằng lock nhỏ, `ConcurrentDictionary`, hoặc quan trọng hơn là cấm concurrent access ở toàn bộ Unit of Work boundary chứ không chỉ ở command execution.

### 5.2.6. `Dispose()` không phối hợp với operation guard

`Dispose()` chỉ kiểm tra `_isDisposed`, không chờ hoặc chặn `_operationInProgress`. Một child task có thể đang execute command trong khi task khác dispose connection.

Nên có state transition nguyên tử và từ chối dispose/completion khi operation đang active, hoặc đợi operation lease kết thúc.

### 5.2.7. Rollback khi dispose phụ thuộc hành vi provider

Nếu caller quên `CommitAsync()`/`RollbackAsync()` rồi gọi `Dispose()`, code dispose transaction nhưng không gọi rollback rõ ràng.

Nhiều provider rollback transaction chưa commit khi dispose, nhưng public abstraction hiện không biểu đạt chính sách này. An toàn hơn là:

- root scope dispose khi chưa complete phải rollback rõ ràng;
- nếu rollback lỗi, vẫn tiếp tục dispose và tổng hợp lỗi;
- ghi nhận trạng thái faulted.

### 5.2.8. Không cấu hình isolation level và transaction options

`BeginTransactionAsync()` luôn gọi `Connection.BeginTransaction()` mặc định. Chưa có:

- isolation level;
- timeout;
- read-only intent;
- cancellation;
- provider-specific transaction options.

Điều này hạn chế khả năng dùng trong workload thật có yêu cầu locking/consistency khác nhau.

## 5.3. Mức ưu tiên P2 — độ trưởng thành của package và quy trình

### 5.3.1. Chưa có CI và trạng thái build tự động

Snapshot hiện tại không có `.github/workflows` và commit không có status check. Vì vậy repository không tự động xác nhận:

- restore/build/test trên push/PR;
- Windows và Linux behavior;
- warnings-as-errors;
- code coverage;
- package build.

README hướng dẫn `--warnaserror`, nhưng project chưa cấu hình `TreatWarningsAsErrors` và không có CI cưỡng chế lệnh đó.

### 5.3.2. Chưa có package metadata và release contract

`UnitOfWork.Core.csproj` chưa có các metadata thường cần cho NuGet:

- `PackageId`;
- `Version`/versioning strategy;
- `Authors`;
- `Description`;
- `RepositoryUrl`;
- `PackageTags`;
- license expression/file;
- XML documentation;
- Source Link;
- symbol package.

Repository cũng chưa có license file, changelog hoặc compatibility policy.

### 5.3.3. Chỉ test SQLite và chỉ target .NET 8

SQLite là lựa chọn tốt cho transaction integration test nhanh, nhưng chưa chứng minh behavior trên:

- SQL Server;
- PostgreSQL;
- MySQL/MariaDB;
- provider có async/disposal semantics khác;
- provider không kế thừa `DbConnection` theo cách mong đợi.

Nếu thư viện được quảng bá là provider-neutral, nên có provider matrix tối thiểu hoặc nêu rõ phạm vi support.

### 5.3.4. Toàn bộ xUnit parallelization bị tắt

Việc tắt parallelization giúp tránh test interference do static ambient state, nhưng đồng thời là dấu hiệu rằng global/static state làm test isolation khó khăn.

Nó cũng có thể che một số vấn đề concurrency giữa test case. Thiết kế production tốt hơn nên cho phép test song song bằng ambient state được key/scoped đúng cách.

### 5.3.5. Một số concurrency test phụ thuộc delay thời gian

`ConcurrencyGuardTests` dùng `Task.Delay(300)` và chờ `50ms` để tạo cửa sổ overlap. Cách này dễ hiểu nhưng có thể flaky trên máy quá tải.

Tốt hơn nên dùng `TaskCompletionSource`/barrier:

1. operation đầu báo đã vào guard;
2. test gọi operation thứ hai;
3. test release operation đầu.

Kết quả sẽ hoàn toàn tất định và chạy nhanh hơn.

## 6. Ví dụ so sánh hành vi hiện tại và thiết kế kỳ vọng

| Tình huống | Hành vi hiện tại | Thiết kế production-grade kỳ vọng |
|---|---|---|
| Outer và inner cùng thành công | Dùng cùng UoW; inner giảm ref-count; outer commit thật. | Giữ nguyên semantics này. |
| Inner thất bại | Inner rollback đặt cờ, outer commit dẫn tới rollback toàn bộ. | Giữ nguyên mặc định; savepoint là option riêng nếu cần. |
| Inner dùng `using` | Có thể dispose transaction/connection của outer vì cùng object. | Inner dispose chỉ đóng lease của inner, không sở hữu root resource. |
| Repository quên `cmd.Transaction` | Command có thể chạy ngoài transaction. | Wrapper tự gắn transaction và từ chối bypass. |
| Caller dùng `uow.Connection` | Đi vòng qua guard và transaction. | Không expose raw connection hoặc chỉ expose facade đã bind. |
| Streaming reader | Guard được release ngay sau `ExecuteReader()`. | Guard giữ tới khi reader dispose. |
| Hai manager cho hai database | Static ambient có thể làm manager B dùng UoW của A. | Ambient state key theo manager/context identity. |
| Gọi commit hai lần | Ref-count có thể âm, transaction có thể bị gọi lại. | State machine fail-fast: “scope already completed”. |
| Commit lỗi | Ref-count đã giảm; trạng thái outcome không rõ. | Chuyển `Faulted`, không retry mù, cleanup deterministic. |
| Clear ambient khi active | Transaction có thể bị orphan. | Chỉ root scope cleanup được ambient và resource cùng lúc. |
| API async | Nhiều operation thực tế vẫn sync. | Dùng ADO.NET async API và cancellation thực sự. |

## 7. Những test quan trọng còn thiếu

Nên bổ sung theo thứ tự ưu tiên:

1. `Raw_Connection_Cannot_Bypass_Guard`.
2. `Command_Is_Automatically_Enlisted_In_Current_Transaction`.
3. `Repository_Command_Without_Manual_Transaction_Is_Rolled_Back`.
4. `Inner_Scope_Dispose_Does_Not_Dispose_Root_UnitOfWork`.
5. `Double_Commit_Throws_Without_Decrementing_Below_Zero`.
6. `Rollback_After_Commit_Throws`.
7. `Commit_Failure_Leaves_Deterministic_Faulted_State`.
8. `ClearCurrent_While_Active_Is_Rejected_Or_Cleans_Up_Safely`.
9. `Two_Manager_Instances_Do_Not_Share_Ambient_UnitOfWork`.
10. `Reader_Holds_Concurrency_Guard_Until_Disposed`.
11. `Concurrent_GetRepository_Is_Rejected_Or_Safe`.
12. `Dispose_During_Active_Operation_Is_Rejected_Or_Waits`.
13. `Dispose_Without_Completion_Explicitly_Rolls_Back`.
14. Cancellation tests cho open/begin/commit/rollback/execute.
15. Integration test với ít nhất SQL Server hoặc PostgreSQL.
16. Test cho Dapper sync và async nếu README tiếp tục tuyên bố hỗ trợ Dapper.

## 8. Lộ trình cải tiến đề xuất

### P0 — khóa chặt transaction invariants

1. Không public raw connection.
2. Tạo transaction-bound guarded connection:
   - tự gắn transaction cho command;
   - chặn transaction khác;
   - chặn `Close`, `Dispose`, `BeginTransaction`, `ChangeDatabase` từ repository.
3. Tách `RootUnitOfWork` và `UnitOfWorkScope`:
   - mỗi `BeginAsync()` trả scope/lease riêng;
   - root duy nhất sở hữu connection/transaction;
   - inner dispose chỉ giảm ref-count;
   - outer dispose kết thúc root.
4. Thêm lifecycle state machine nguyên tử.
5. Gộp clear ambient vào root cleanup; bỏ hoặc hạn chế `ClearCurrent()` public.
6. Key ambient state theo manager/context identity.

### P1 — hoàn thiện async và concurrency boundary

1. Dùng `DbConnection`/`DbTransaction`/`DbCommand` async khi provider hỗ trợ.
2. Thêm `CancellationToken` cho toàn bộ I/O operation.
3. Implement `IAsyncDisposable`.
4. Guard toàn bộ reader lifetime bằng wrapper/operation lease.
5. Guard hoặc serialize `GetRepository<T>()` và lifecycle operations.
6. Thêm `UnitOfWorkOptions` cho isolation level và transaction configuration.
7. Xác định rõ policy khi commit/rollback/dispose lỗi.

### P2 — package và vận hành

1. Thêm GitHub Actions build/test trên Windows và Linux.
2. Bật nullable warnings/analyzers và warnings-as-errors trong CI.
3. Thêm coverage report và threshold hợp lý.
4. Thêm NuGet metadata, license, Source Link và versioning.
5. Thêm provider compatibility matrix.
6. Thêm benchmark cho overhead của wrappers/AsyncLocal/guard.
7. Bổ sung README với usage pattern an toàn và anti-pattern.

## 9. Gợi ý public API an toàn hơn

Một hướng API có thể là:

```csharp
public interface IUnitOfWorkScope : IAsyncDisposable
{
    IDbConnection Connection { get; }
    IDbTransaction Transaction { get; }

    T GetRepository<T>() where T : class;
    ValueTask CompleteAsync(CancellationToken cancellationToken = default);
}

public interface IUnitOfWorkManager
{
    IUnitOfWorkScope? Current { get; }

    ValueTask<IUnitOfWorkScope> BeginAsync(
        UnitOfWorkOptions? options = null,
        CancellationToken cancellationToken = default);
}
```

Semantics đề xuất:

- mỗi `BeginAsync()` trả một scope object khác nhau;
- root scope tạo transaction;
- nested scope chỉ tăng ref-count;
- `CompleteAsync()` tương đương “scope này thành công”;
- dispose scope chưa complete sẽ đánh dấu rollback;
- chỉ khi scope cuối cùng dispose thì root commit hoặc rollback;
- caller không cần gọi `ClearCurrent()`;
- connection trả ra luôn là facade đã bind transaction;
- root resource được cleanup một lần duy nhất.

Cách này giảm đáng kể khả năng sử dụng sai và gần với pattern scope/lease quen thuộc hơn là trả chung một mutable object cho mọi tầng.

## 10. Kết luận

Repository có nhiều điểm đáng ghi nhận:

- bài toán được giới hạn tốt;
- nested UoW và rollback propagation rõ ràng;
- ambient initialization xử lý tốt race/reentrancy;
- error cleanup cẩn thận;
- test bằng database thật và chú ý Windows resource leak;
- concurrency violation được báo sớm với thông báo hữu ích.

Điểm yếu chính không nằm ở ý tưởng ref-counting mà nằm ở việc **public API chưa bảo vệ các invariant của ý tưởng đó**. Hiện tại một caller hoặc repository chỉ cần quên gắn transaction, dùng raw connection, dispose ở tầng trong hoặc clear ambient sai thời điểm là có thể phá tính nguyên tử và vòng đời transaction.

Vì vậy, bước cải tiến quan trọng nhất là chuyển từ mô hình “mọi tầng dùng chung một UnitOfWork object và phải tuân thủ quy ước” sang mô hình “root sở hữu resource, mỗi tầng nhận một scope lease có quyền hạn giới hạn”. Sau khi khóa được transaction binding, lifecycle state và ambient isolation, các phần async, provider support và package maturity có thể được hoàn thiện theo sau.
