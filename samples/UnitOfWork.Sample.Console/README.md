# UnitOfWork.Sample.Console

Sample .NET 8 dùng SQLite file-based thật để minh họa cách sử dụng an toàn
`UnitOfWork.Core`.

## Chạy sample

```powershell
dotnet run --project samples/UnitOfWork.Sample.Console/UnitOfWork.Sample.Console.csproj
```

Process trả exit code `0` khi tất cả scenario thành công và `1` khi có scenario
thất bại hoặc có exception ngoài dự kiến.

## Các scenario

1. Outer service và nested inner service insert `10`, `20` rồi commit chung một
   root transaction.
2. Inner scope insert `40` nhưng không gọi `CompleteAsync()`, khiến cả transaction
   chứa `30`, `40` rollback dù outer scope gọi `CompleteAsync()`.
3. Hai lần `GetRepository<ICounterRepository>()` trong cùng root trả cùng một
   instance đã cache.
4. Command tạo từ transaction-bound connection nhận timeout `15` giây từ
   `UnitOfWorkOptions`.
5. `BeginAsync()` quan sát cancellation token đã cancel.
6. Command thứ hai bị từ chối trong lúc streaming reader còn giữ operation lease.

Repository không tự gán `command.Transaction`; command tạo từ connection facade
đã được enlist vào root transaction.

Không chạy nhiều command trên cùng scope bằng `Task.WhenAll`. Các command dùng
chung root connection/transaction phải chạy tuần tự và reader phải được dispose
trước operation tiếp theo.

## Kết quả mong đợi

```text
[PASS] Nested commit: Committed values: [10, 20]
[PASS] Repository cache: Two GetRepository<T>() calls returned the same root-scoped instance.
[PASS] Command timeout: Observed 15 seconds.
[PASS] Incomplete nested scope: Committed values remain: [10, 20]
[PASS] Cancellation: A pre-canceled BeginAsync token was observed.
[PASS] Reader concurrency guard: A second command was rejected while a reader held the operation lease.
All sample scenarios passed.
```
