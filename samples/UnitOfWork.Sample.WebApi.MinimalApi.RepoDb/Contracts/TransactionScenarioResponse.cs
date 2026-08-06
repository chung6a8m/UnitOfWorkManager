using UnitOfWork.Sample.WebApi.MinimalApi.Models;

namespace UnitOfWork.Sample.WebApi.MinimalApi.Contracts;

public sealed record TransactionScenarioResponse(
    string Scenario,
    bool Committed,
    IReadOnlyList<CounterItem> Items);
