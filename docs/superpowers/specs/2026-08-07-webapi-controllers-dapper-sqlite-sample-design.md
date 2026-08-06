# ASP.NET Core Controllers + Dapper + SQLite Sample Design

## Goal

Add a runnable .NET 8 ASP.NET Core Web API sample that demonstrates `UnitOfWork.Core` through MVC controllers, application services, Dapper repositories, and a real file-based SQLite database.

## Scope

The sample exposes four endpoints:

1. `GET /api/counters` reads all committed rows through Dapper.
2. `POST /api/counters/commit` writes an outer and nested inner value, completing both scopes so one root transaction commits.
3. `POST /api/counters/rollback` writes an outer and nested inner value, but the inner scope is disposed incomplete so the whole root transaction rolls back.
4. `GET /api/counters/diagnostics` demonstrates root-scoped repository caching, command-timeout propagation, pre-canceled `BeginAsync()` handling with ambient cleanup, and reader-lifetime concurrency rejection.

The sample does not add authentication, an ORM, savepoints, a DI framework beyond built-in ASP.NET Core DI, or provider-specific transaction options.

## Architecture

The project lives at `samples/UnitOfWork.Sample.WebApi.Controllers` and targets `net8.0` with `Microsoft.NET.Sdk.Web`.

- `Program.cs` configures controllers, problem details, the SQLite database singleton, one `UnitOfWorkManager`, and scoped application services.
- `SqliteSampleDatabase` owns a unique temporary SQLite file for the application lifetime, creates provider connections, initializes the schema, and deletes database sidecars on host shutdown.
- `DapperCounterRepository` receives only the transaction-bound `DbConnection`. It uses Dapper `CommandDefinition` with request cancellation tokens and never supplies a separate transaction.
- `NestedCounterService` owns nested service boundaries.
- `CounterApplicationService` orchestrates root scopes, commit/rollback scenarios, reads, and diagnostics.
- `CountersController` maps HTTP requests to the application service and passes `HttpContext.RequestAborted` through every operation.

## Data and Transaction Flow

The SQLite schema is:

```sql
CREATE TABLE Counter
(
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Value INTEGER NOT NULL
);
```

The commit endpoint inserts both request values in nested scopes that share one root transaction. The rollback endpoint inserts both values, disposes the nested scope without `CompleteAsync()`, and then completes the outer scope; the root observes the rollback request and discards both rows.

All reads occur in a new Unit of Work after the write root has finalized, so responses contain only committed data.

## HTTP Contracts

`POST /api/counters/commit` and `POST /api/counters/rollback` accept:

```json
{
  "outerValue": 10,
  "innerValue": 20
}
```

They return a scenario name, whether data committed, and the complete committed counter list.

`GET /api/counters/diagnostics` returns:

- `repositoryCacheReused`
- `commandTimeoutSeconds`
- `cancellationObserved`
- `ambientClearedAfterCancellation`
- `concurrencyGuardObserved`
- `allPassed`

## Error Handling

ASP.NET Core problem details handle unexpected exceptions. Expected cancellation and `UnitOfWorkConcurrencyException` are caught only inside the diagnostics probe that intentionally demonstrates those behaviors.

Controller actions do not catch request cancellation. A canceled client request propagates through the service, Unit of Work, and Dapper command via the action cancellation token.

## Testing

A separate project at `tests/UnitOfWork.Sample.WebApi.Tests` uses `WebApplicationFactory<Program>` and one sequential end-to-end test. The test:

1. verifies the initial list is empty;
2. commits values `10` and `20`;
3. attempts to roll back values `30` and `40` and verifies they are absent;
4. verifies the diagnostics endpoint reports all expected behaviors.

The test project uses `Microsoft.AspNetCore.Mvc.Testing` 8.0.29. The Web API uses Dapper 2.1.79 and aligns its SQLite package with the existing repository version, 8.0.10.
