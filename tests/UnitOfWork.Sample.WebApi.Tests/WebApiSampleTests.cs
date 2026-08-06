using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace UnitOfWork.Sample.WebApi.Tests;

public sealed class WebApiSampleTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public WebApiSampleTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Endpoints_Demonstrate_Commit_Rollback_And_Diagnostics()
    {
        var initialItems = await _client.GetFromJsonAsync<CounterItem[]>("/api/counters");
        Assert.NotNull(initialItems);
        Assert.Empty(initialItems);

        using var commitResponse = await _client.PostAsJsonAsync(
            "/api/counters/commit",
            new { outerValue = 10, innerValue = 20 });
        commitResponse.EnsureSuccessStatusCode();

        var committed = await commitResponse.Content
            .ReadFromJsonAsync<TransactionScenarioResponse>();
        Assert.NotNull(committed);
        Assert.True(committed.Committed);
        Assert.Equal(new[] { 10, 20 }, committed.Items.Select(item => item.Value));

        using var rollbackResponse = await _client.PostAsJsonAsync(
            "/api/counters/rollback",
            new { outerValue = 30, innerValue = 40 });
        rollbackResponse.EnsureSuccessStatusCode();

        var rolledBack = await rollbackResponse.Content
            .ReadFromJsonAsync<TransactionScenarioResponse>();
        Assert.NotNull(rolledBack);
        Assert.False(rolledBack.Committed);
        Assert.Equal(new[] { 10, 20 }, rolledBack.Items.Select(item => item.Value));

        var finalItems = await _client.GetFromJsonAsync<CounterItem[]>("/api/counters");
        Assert.NotNull(finalItems);
        Assert.Equal(new[] { 10, 20 }, finalItems.Select(item => item.Value));

        var diagnostics = await _client.GetFromJsonAsync<DiagnosticsResponse>(
            "/api/counters/diagnostics");
        Assert.NotNull(diagnostics);
        Assert.True(diagnostics.RepositoryCacheReused);
        Assert.Equal(15, diagnostics.CommandTimeoutSeconds);
        Assert.True(diagnostics.CancellationObserved);
        Assert.True(diagnostics.AmbientClearedAfterCancellation);
        Assert.True(diagnostics.ConcurrencyGuardObserved);
        Assert.True(diagnostics.AllPassed);
    }

    private sealed record CounterItem(long Id, int Value);

    private sealed record TransactionScenarioResponse(
        string Scenario,
        bool Committed,
        CounterItem[] Items);

    private sealed record DiagnosticsResponse(
        bool RepositoryCacheReused,
        int CommandTimeoutSeconds,
        bool CancellationObserved,
        bool AmbientClearedAfterCancellation,
        bool ConcurrencyGuardObserved,
        bool AllPassed);
}
