using UnitOfWork.Tests.Fixtures;
using Xunit;

namespace UnitOfWork.Tests;

public sealed class SqliteTestDbTests
{
    [Fact]
    public void Database_File_Is_Created_In_The_System_Temp_Directory()
    {
        using var db = new SqliteTestDb();

        var tempDirectory = Path.GetFullPath(Path.GetTempPath());
        var databasePath = Path.GetFullPath(db.DatabasePath);
        var relativePath = Path.GetRelativePath(tempDirectory, databasePath);

        Assert.False(Path.IsPathRooted(relativePath));
        Assert.NotEqual("..", relativePath);
        Assert.False(relativePath.StartsWith(
            $"..{Path.DirectorySeparatorChar}",
            StringComparison.Ordinal));
    }

}
