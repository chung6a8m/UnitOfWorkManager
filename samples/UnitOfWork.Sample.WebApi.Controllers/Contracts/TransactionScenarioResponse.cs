using UnitOfWork.Sample.WebApi.Models;

namespace UnitOfWork.Sample.WebApi.Contracts;

public sealed record TransactionScenarioResponse(
    string Scenario,
    bool Committed,
    IReadOnlyList<CounterItem> Items);
