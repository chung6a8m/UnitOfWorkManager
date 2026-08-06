using UnitOfWork.Sample.ConsoleApp;
using Xunit;

namespace UnitOfWork.Tests;

public class ConsoleSampleTests
{
    [Fact]
    public async Task RunAsync_Demonstrates_All_Supported_Scenarios()
    {
        using var output = new StringWriter();

        var summary = await SampleApplication.RunAsync(output);

        Assert.True(summary.CommitPersisted, output.ToString());
        Assert.True(summary.NestedIncompleteRolledBack, output.ToString());
        Assert.True(summary.RepositoryCacheReused, output.ToString());
        Assert.Equal(15, summary.ObservedCommandTimeoutSeconds);
        Assert.True(summary.CancellationObserved, output.ToString());
        Assert.True(summary.ConcurrencyGuardObserved, output.ToString());
        Assert.Equal(new[] { 10, 20 }, summary.FinalValues.ToArray());
        Assert.True(summary.AllPassed, output.ToString());
        Assert.Contains("All sample scenarios passed.", output.ToString());
    }
}
