using UnitOfWork.Core;
using CoreUoW = UnitOfWork.Core.UnitOfWork;

namespace UnitOfWork.Tests.Fixtures;

/// <summary>
/// xUnit tạo 1 instance MỚI của test class cho MỖI [Fact], và luôn gọi Dispose() sau khi
/// test đó chạy xong — dù test pass hay fail/throw. Kế thừa lớp này đảm bảo 2 AsyncLocal
/// *tĩnh* (dùng chung toàn process) luôn được dọn sạch giữa các test, kể cả khi 1 test fail
/// giữa chừng và bỏ lỡ bước cleanup (Dispose()/ClearCurrent()) của chính nó — tránh trạng
/// thái cũ rò rỉ sang test kế tiếp và gây lỗi khó hiểu (ví dụ: BeginAsync() ở test sau vô
/// tình "kế thừa" UnitOfWork đã đóng của test trước).
/// </summary>
public abstract class UnitOfWorkTestBase : IDisposable
{
    public void Dispose()
    {
        CoreUoW.AmbientFlowId.Value = null;
        UnitOfWorkManager.ResetAmbientStateForTests();
    }
}
