# Console SQLite Sample Design

## Goal

Add a runnable .NET 8 console application that demonstrates the safe, production-facing usage patterns of `UnitOfWork.Core` with a real file-based SQLite database.

## Scope

The sample demonstrates six behaviors:

1. An outer service and a nested inner service share one root transaction and commit together.
2. Disposing a nested scope without `CompleteAsync()` requests rollback for the entire root transaction.
3. Repeated `GetRepository<T>()` calls within one root return the cached repository instance.
4. `UnitOfWorkOptions.CommandTimeoutSeconds` is applied to commands created from the transaction-bound connection.
5. A canceled token passed to `BeginAsync()` is observed without publishing a usable ambient root.
6. A second command is rejected while a streaming reader still holds the operation lease.

The sample does not add a dependency injection container, Dapper, an ORM, savepoints, provider-specific transaction factories, or interactive input.

## Architecture

The new project lives at `samples/UnitOfWork.Sample.Console` and references `src/UnitOfWork.Core` plus `Microsoft.Data.Sqlite` 8.0.10.

- `SqliteSampleDatabase` owns the temporary SQLite file, initializes the schema, creates provider connections, reads committed values independently, and deletes database sidecars during cleanup.
- `CounterRepository` receives only the transaction-bound `DbConnection`. It creates commands without assigning `DbCommand.Transaction` manually.
- `CounterService` contains the outer and nested business operations used by the commit and rollback demonstrations.
- `SampleApplication` orchestrates all scenarios, writes human-readable PASS/FAIL output, and returns a structured `SampleSummary`.
- `Program.cs` runs the application and sets a non-zero process exit code when any scenario fails.

## Data Flow

Each scenario starts a fresh root scope through the same `UnitOfWorkManager`. The manager creates a SQLite connection, starts the transaction, and supplies a transaction-bound connection facade to repositories and direct ADO.NET commands.

The commit scenario inserts values `10` and `20`. The rollback scenario attempts to insert `30` and `40`, but the inner scope is disposed incomplete, so the final committed database remains `[10, 20]`.

Committed-state verification always uses an independent SQLite connection rather than the active Unit of Work connection.

## Error Handling

Expected cancellation and concurrency exceptions are caught only by the scenarios that intentionally demonstrate them. Unexpected exceptions escape `SampleApplication.RunAsync`, cause the console process to fail, and remain visible to the user.

Temporary database cleanup clears SQLite pools and retries deletion of `.db`, `-journal`, `-wal`, and `-shm` files so resource leaks remain detectable.

## Testing

`tests/UnitOfWork.Tests/ConsoleSampleTests.cs` references the executable sample project and invokes `SampleApplication.RunAsync` directly. The test asserts that every scenario passed, the configured command timeout was observed, and the final committed values are exactly `10` and `20`.

The solution and root README are updated so the sample is discoverable and runnable with:

```powershell
dotnet run --project samples/UnitOfWork.Sample.Console/UnitOfWork.Sample.Console.csproj
```
