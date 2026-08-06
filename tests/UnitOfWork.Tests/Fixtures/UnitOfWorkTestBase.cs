using CoreUoW = UnitOfWork.Core.UnitOfWork;

namespace UnitOfWork.Tests.Fixtures;

/// <summary>
/// xUnit tạo 1 instance MỚI của test class cho MỖI [Fact], và luôn gọi Dispose() sau khi
/// test đó chạy xong — dù test pass hay fail/throw. Kế thừa lớp này dọn AsyncLocal của
/// implementation legacy còn được các test trực tiếp sử dụng.
/// </summary>
public abstract class UnitOfWorkTestBase : IDisposable
{
    public void Dispose()
    {
        CoreUoW.AmbientFlowId = null;
    }
}
