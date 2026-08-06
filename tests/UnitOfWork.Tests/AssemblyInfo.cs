using Xunit;

// UnitOfWork.AmbientFlowId và UnitOfWorkManager._current đều là AsyncLocal *tĩnh* (static),
// dùng chung cho toàn bộ process test. Nếu xUnit chạy các test class song song trên nhiều
// thread, các flow AsyncLocal có thể "lẫn" vào nhau và test sẽ flaky ngẫu nhiên.
// -> Tắt hẳn song song hóa để mọi test chạy tuần tự, đảm bảo AsyncLocal sạch giữa các case.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
