# ASP.NET Core Controllers + Dapper + SQLite Sample Plan

**Date:** 2026-08-07  
**Status:** Implementation written; build and test verification pending in a .NET 8 environment.

## Goal

Add a runnable .NET 8 ASP.NET Core Web API sample that demonstrates
`UnitOfWork.Core` through MVC controllers, application services, nested service
boundaries, Dapper repositories, and a real file-based SQLite database.

## Architecture

The sample lives at `samples/UnitOfWork.Sample.WebApi.Controllers` and keeps each
responsibility separate:

```text
CountersController
    -> CounterApplicationService
        -> NestedCounterService
        -> ICounterRepository
            -> DapperCounterRepository
                -> transaction-bound DbConnection
                    -> SQLite
```

- `Program.cs` configures controllers, problem details, a singleton temporary
  SQLite database, a singleton `UnitOfWorkManager`, and scoped services.
- `SqliteSampleDatabase` owns the database file for the host lifetime, creates
  provider connections, initializes the schema, and removes database sidecars
  during host disposal.
- `DapperCounterRepository` receives only the transaction-bound `DbConnection`,
  uses `CommandDefinition` cancellation tokens, and never receives or assigns a
  separate transaction.
- `CounterApplicationService` owns root scopes and HTTP-level scenarios.
- `NestedCounterService` opens nested scopes to demonstrate shared-root commit
  and rollback propagation.
- `WebApiApplicationFactory` forces the `Testing` environment so integration
  tests are independent from HTTPS redirection and machine configuration.

## Technology

- .NET 8
- ASP.NET Core MVC Controllers
- Dapper 2.1.79
- Microsoft.Data.Sqlite 8.0.10
- Microsoft.AspNetCore.Mvc.Testing 8.0.29
- xUnit 2.9.2

## Constraints

- Use MVC controllers, not Minimal APIs.
- Use built-in ASP.NET Core dependency injection.
- Pass request cancellation through controllers, services, `BeginAsync`,
  `CompleteAsync`, and Dapper `CommandDefinition`.
- Never pass a transaction argument to Dapper.
- Never assign `DbCommand.Transaction` manually.
- Use `await using` for scopes and async ADO.NET resources.
- Use file-based SQLite with `Pooling=False`.
- Keep committed values exactly `10` and `20`.
- Ensure attempted values `30` and `40` are rolled back.
- Keep this sample independent from the existing Console sample.

## HTTP Contract

### `GET /api/counters`

Returns all committed rows ordered by `Id`.

### `POST /api/counters/commit`

Request:

```json
{
  "outerValue": 10,
  "innerValue": 20
}
```

The outer and nested scopes both call `CompleteAsync()`, causing the shared root
transaction to commit.

### `POST /api/counters/rollback`

Request:

```json
{
  "outerValue": 30,
  "innerValue": 40
}
```

The inner scope is disposed without `CompleteAsync()`. The root records a
rollback request, so completing the outer scope rolls back both values.

### `GET /api/counters/diagnostics`

Returns:

- `repositoryCacheReused`
- `commandTimeoutSeconds`
- `cancellationObserved`
- `ambientClearedAfterCancellation`
- `concurrencyGuardObserved`
- `allPassed`

The concurrency probe keeps a streaming reader open and invokes a Dapper count
command on the same root. `UnitOfWorkConcurrencyException` must be observed.

## Work Packages

### P1 — Integration test first

**Files:**

- `tests/UnitOfWork.Sample.WebApi.Tests/UnitOfWork.Sample.WebApi.Tests.csproj`
- `tests/UnitOfWork.Sample.WebApi.Tests/WebApiSampleTests.cs`

**Acceptance:**

- The test project references the Web API project.
- `WebApplicationFactory<Program>` hosts the application.
- The test verifies initial empty state, commit, rollback, final values, and all
  diagnostics.
- The factory explicitly uses environment `Testing`.

### P2 — Database and Dapper repository

**Files:**

- `Infrastructure/SqliteSampleDatabase.cs`
- `Models/CounterItem.cs`
- `Repositories/ICounterRepository.cs`
- `Repositories/DapperCounterRepository.cs`

**Acceptance:**

- SQLite schema is created once per host.
- Provider connections are returned closed for Unit of Work ownership.
- Dapper uses parameterized commands and cancellation tokens.
- Commands rely on the transaction-bound connection instead of a transaction
  argument.
- Temporary files are removed with retry during disposal.

### P3 — Services and controllers

**Files:**

- `Contracts/CreateCounterPairRequest.cs`
- `Contracts/TransactionScenarioResponse.cs`
- `Contracts/DiagnosticsResponse.cs`
- `Services/NestedCounterService.cs`
- `Services/CounterApplicationService.cs`
- `Controllers/CountersController.cs`

**Acceptance:**

- Commit flow persists only `10` and `20`.
- Rollback flow discards `30` and `40`.
- Reads start a new Unit of Work after write finalization.
- Diagnostics verify cache identity, timeout `15`, cancellation cleanup, and
  reader-lifetime concurrency rejection.
- Controller actions pass their cancellation token without replacing it.

### P4 — Host configuration

**Files:**

- `Program.cs`
- `UnitOfWork.Sample.WebApi.Controllers.csproj`
- `UnitOfWork.Sample.WebApi.Controllers.http`

**Acceptance:**

- `Program` is public and partial for `WebApplicationFactory<Program>`.
- Controllers and problem details are registered.
- One `UnitOfWorkManager` is registered as singleton; its `AsyncLocal` state
  remains isolated per request execution flow.
- HTTPS redirection is skipped in `Development` and `Testing`.
- The `.http` file includes all four endpoints.

### P5 — Documentation and solution integration

**Files:**

- `samples/UnitOfWork.Sample.WebApi.Controllers/README.md`
- `README.md`
- `UnitOfWork.slnx`

**Acceptance:**

- The sample and test projects appear in the solution.
- Root documentation distinguishes Console and Controllers samples.
- The sample README explains architecture, requests, responses, cancellation,
  Dapper transaction ownership, and concurrency rules.

## Verification

Run on Windows with .NET 8 SDK:

```powershell
dotnet restore UnitOfWork.slnx
dotnet build UnitOfWork.slnx --no-restore --warnaserror
dotnet test UnitOfWork.slnx --no-build --logger "console;verbosity=normal"
powershell -ExecutionPolicy Bypass -File scripts/verify-p0-transaction-invariants.ps1
powershell -ExecutionPolicy Bypass -File scripts/verify-p1-async-concurrency-boundary.ps1
```

Required result: all commands exit successfully with no build warnings and no
test failures. Until this verification is run, the implementation must remain in
a Draft pull request and must not be described as build/test passing.
