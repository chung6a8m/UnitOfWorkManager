# ASP.NET Core Minimal API + RepoDb + SQLite Sample Design

## Goal

Add a runnable .NET 8 ASP.NET Core Minimal API sample that demonstrates
`UnitOfWork.Core` through endpoint mappings, application services, nested
service boundaries, RepoDb operations, and a real file-based SQLite database.

## Scope

The sample is independent from the existing Console and Controllers/Dapper
samples. It exposes four endpoints:

1. `GET /api/counters` reads committed rows through RepoDb.
2. `POST /api/counters/commit` writes an outer and nested inner value, completing
   both scopes so one shared root transaction commits.
3. `POST /api/counters/rollback` writes an outer and nested inner value, but the
   inner scope is disposed incomplete so the whole root transaction rolls back.
4. `GET /api/counters/diagnostics` demonstrates root-scoped repository caching,
   command-timeout propagation, pre-canceled `BeginAsync()` cleanup, and
   reader-lifetime concurrency rejection when a RepoDb operation overlaps an
   open reader on the same root.

The sample does not add authentication, OpenAPI generation, savepoints, or a
shared infrastructure project between samples.

## Architecture

The project lives at `samples/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb`.

```text
Minimal API endpoints
    -> CounterApplicationService
        -> NestedCounterService
        -> ICounterRepository
            -> RepoDbCounterRepository
                -> transaction-bound DbConnection
                    -> SQLite
```

- `Program.cs` initializes the RepoDb SQLite provider once, configures problem
  details, the temporary SQLite database, one `UnitOfWorkManager`, and services.
- `CounterEndpoints` maps the four route handlers and passes
  `HttpContext.RequestAborted` to the application service.
- `SqliteSampleDatabase` owns one unique temporary SQLite file for the host
  lifetime, creates provider connections, initializes the schema, and removes
  database sidecars during disposal.
- `RepoDbCounterRepository` receives only the transaction-bound `DbConnection`.
  It uses RepoDb `InsertAsync`, `ExecuteQueryAsync`, and `ExecuteScalarAsync`
  without supplying a separate transaction.
- `NestedCounterService` owns nested service boundaries.
- `CounterApplicationService` orchestrates root scopes, reads, commit/rollback
  scenarios, and diagnostics.

## Dependencies

- `RepoDb` 1.15.1.
- `RepoDb.Sqlite.Microsoft` 1.15.0.
- The provider transitively requires `Microsoft.Data.Sqlite` 10.0.9 or newer.
- `Microsoft.AspNetCore.Mvc.Testing` 8.0.29 for integration tests.

The application initializes the provider exactly once with:

```csharp
RepoDb.SqliteBootstrap.Initialize();
```

## RepoDb Transaction Rule

RepoDb operations receive only the connection exposed by the current Unit of
Work scope. They never receive RepoDb's optional `transaction` argument and the
sample never assigns `DbCommand.Transaction` manually. Commands created from the
transaction-bound connection are enlisted by `UnitOfWork.Core`.

RepoDb async extension methods in the selected API do not expose a
`CancellationToken`. The repository therefore checks request cancellation before
and after each RepoDb operation. Unit of Work begin and completion receive the
actual request token. The documentation must state that an in-flight RepoDb
command cannot be canceled through these extension-method overloads.

## Data and HTTP Contracts

SQLite schema:

```sql
CREATE TABLE Counter
(
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Value INTEGER NOT NULL
);
```

Commit and rollback endpoints accept:

```json
{
  "outerValue": 10,
  "innerValue": 20
}
```

Responses contain a scenario name, a committed flag, and all currently committed
counter rows.

Diagnostics returns:

- `repositoryCacheReused`
- `commandTimeoutSeconds`
- `cancellationObserved`
- `ambientClearedAfterCancellation`
- `concurrencyGuardObserved`
- `allPassed`

## Error Handling

ASP.NET Core exception handling and problem details handle unexpected failures.
The intentional cancellation and concurrency probes catch only their expected
exceptions inside the diagnostics service.

Endpoint handlers do not swallow request cancellation. Validation rejects a
missing body or values outside the `Int32` JSON contract through normal Minimal
API binding behavior.

## Testing

A separate project at
`tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests` uses
`WebApplicationFactory<Program>` with environment `Testing`.

One sequential end-to-end test verifies:

1. the initial list is empty;
2. values `10` and `20` commit;
3. values `30` and `40` roll back;
4. the final list contains only `10` and `20`;
5. all diagnostics report the expected Unit of Work behavior.

The PR remains draft until restore, build, tests, and existing invariant scripts
run successfully in a .NET 8 environment.
