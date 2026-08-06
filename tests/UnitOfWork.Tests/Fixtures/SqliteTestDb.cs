using System.Data;
using Microsoft.Data.Sqlite;
using UnitOfWork.Core;

namespace UnitOfWork.Tests.Fixtures;

/// <summary>
/// Mỗi test tạo 1 file SQLite riêng (không share) — cho phép nhiều IDbConnection cùng lúc
/// đọc/ghi thật sự (transaction thật), và kiểm tra được dữ liệu đã Commit/Rollback bằng
/// cách mở 1 connection RIÊNG BIỆT để đọc lại sau khi UnitOfWork đã Dispose.
/// </summary>
public sealed class SqliteTestDb : IDbConnectionFactory, IDisposable
{
    private readonly string _dbPath;

    internal string DatabasePath => _dbPath;

    public SqliteTestDb()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"uow_test_{Guid.NewGuid():N}.db");

        using var conn = new SqliteConnection(BuildConnectionString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE Counter (Id INTEGER PRIMARY KEY, Value INTEGER NOT NULL);";
        cmd.ExecuteNonQuery();
    }

    // Pooling=False: Dispose() trên SqliteConnection sẽ đóng handle file THẬT SỰ ngay lập tức,
    // thay vì giữ lại trong pool nội bộ của Microsoft.Data.Sqlite chờ tái sử dụng.
    //
    // KHÔNG bật journal_mode=WAL: ở chế độ rollback-journal MẶC ĐỊNH, một connection khác vẫn
    // đọc được dữ liệu (snapshot trước transaction) trong lúc 1 connection đang giữ transaction
    // ghi dở (RESERVED lock không chặn reader, chỉ chặn writer khác) — nên WAL không thật sự
    // cần thiết cho các test này. Bật WAL lại tạo thêm file phụ (-wal, -shm) — trên Windows,
    // các file phụ này đôi khi vẫn bị OS giữ khoá một nhịp sau khi connection đã Dispose(),
    // khiến File.Delete() báo "đang được dùng bởi tiến trình khác". Bỏ WAL loại luôn nguồn gây
    // khoá file đó.
    private string BuildConnectionString() => $"Data Source={_dbPath};Pooling=False;";

    public IDbConnection CreateConnection()
    {
        var conn = new SqliteConnection(BuildConnectionString());
        conn.Open();
        Exec(conn, "PRAGMA busy_timeout=5000;");
        return conn;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Mở 1 connection mới, độc lập, chỉ để đọc lại dữ liệu đã persist (verify commit/rollback).
    /// An toàn để đọc kể cả khi có transaction ghi khác đang mở (chưa commit) trên connection kia,
    /// vì RESERVED lock của rollback-journal mode không chặn reader.</summary>
    public int CountRows()
    {
        using var conn = new SqliteConnection(BuildConnectionString());
        conn.Open();
        Exec(conn, "PRAGMA busy_timeout=5000;");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Counter;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        // Trên Windows, Explorer/AV/indexer đôi khi giữ handle ngắn hạn trên file vừa đóng
        // -> retry vài lần thay vì fail cứng ngay lần đầu. Nếu vẫn không xoá được sau cùng,
        // đây là dấu hiệu THẬT của leak (một UnitOfWork/SqliteConnection nào đó trong test
        // chưa được Dispose) — lúc đó nên fail để lộ ra, không nuốt lỗi âm thầm.
        
        DeleteWithRetry(_dbPath);
        DeleteWithRetry(_dbPath + "-journal"); // rollback-journal mode: sidecar trong lúc có transaction dở, tự dọn khi commit/rollback xong, nhưng vẫn quét phòng hờ
        DeleteWithRetry(_dbPath + "-wal");
        DeleteWithRetry(_dbPath + "-shm");
    }

    private static void DeleteWithRetry(string path, int attempts = 10, int delayMs = 150)
    {
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            catch (IOException) when (i < attempts - 1)
            {
                Thread.Sleep(delayMs);
            }
        }
    }
}
