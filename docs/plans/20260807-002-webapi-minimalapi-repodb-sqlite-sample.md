# ASP.NET Core Minimal API + RepoDb + SQLite Sample Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a runnable and integration-tested .NET 8 Minimal API sample using RepoDb, SQLite, and `UnitOfWork.Core`.

**Architecture:** A standalone Web API project separates endpoint mapping, application orchestration, nested service boundaries, RepoDb repository access, and SQLite file ownership. A dedicated integration-test project hosts the application with `WebApplicationFactory<Program>` and verifies the HTTP behavior end to end.

**Tech Stack:** .NET 8, ASP.NET Core Minimal API, RepoDb 1.15.1, RepoDb.Sqlite.Microsoft 1.15.0, Microsoft.Data.Sqlite 10.0.9 transitively, Microsoft.AspNetCore.Mvc.Testing 8.0.29, xUnit 2.9.2.

## Global Constraints

- Use Minimal API route mappings, not MVC controllers.
- Keep the sample independent from the Console and Controllers/Dapper samples.
- Initialize RepoDb exactly once with `RepoDb.SqliteBootstrap.Initialize()`.
- Pass request cancellation to Unit of Work begin and completion.
- Check cancellation before and after RepoDb operations because the selected RepoDb async overloads do not accept `CancellationToken`.
- Never pass RepoDb's optional transaction argument.
- Never assign `DbCommand.Transaction` manually.
- Use `await using` for Unit of Work scopes and asynchronous ADO.NET resources.
- Use file-based SQLite with `Pooling=False`.
- Keep successful committed values exactly `10` and `20`; attempted values `30` and `40` must roll back.
- Keep the pull request draft until build and test commands run successfully.

---

### Task 1: Specify HTTP behavior first

**Files:**
- Create: `tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests.csproj`
- Create: `tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests/WebApiSampleTests.cs`

**Interfaces:**
- Consumes: public partial `Program` from the sample project.
- Produces: one sequential integration test for list, commit, rollback, and diagnostics.

- [ ] **Step 1: Create the test project**

Reference `Microsoft.NET.Test.Sdk` 17.11.1, xUnit 2.9.2, xUnit runner 2.8.2, `Microsoft.AspNetCore.Mvc.Testing` 8.0.29, coverlet 6.0.2, and the sample project.

- [ ] **Step 2: Write the failing integration test**

The test must force environment `Testing`, assert the initial list is empty, commit `10` and `20`, attempt to roll back `30` and `40`, verify only `10` and `20 remain`, and assert every diagnostics flag including `AllPassed`.

- [ ] **Step 3: Verify RED**

```powershell
dotnet test tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests.csproj
```

Expected: build failure because the referenced sample project does not exist yet.

- [ ] **Step 4: Commit the test specification**

```powershell
git add tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests
git commit -m "test: specify Minimal API RepoDb sample behavior"
```

### Task 2: Add database ownership and RepoDb repository

**Files:**
- Create: `samples/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.csproj`
- Create: `samples/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb/Infrastructure/SqliteSampleDatabase.cs`
- Create: `samples/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb/Models/CounterItem.cs`
- Create: `samples/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb/Repositories/ICounterRepository.cs`
- Create: `samples/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb/Repositories/RepoDbCounterRepository.cs`

**Interfaces:**
- Produces: `SqliteSampleDatabase : IDbConnectionFactory, IDisposable, IAsyncDisposable`.
- Produces: repository methods `InsertAsync`, `ListAsync`, `CountAsync`, and `GetConfiguredCommandTimeoutSeconds`.

- [ ] **Step 1: Create the Web SDK project**

Reference `UnitOfWork.Core`, RepoDb 1.15.1, and RepoDb.Sqlite.Microsoft 1.15.0.

- [ ] **Step 2: Implement SQLite lifetime ownership**

Create a unique temporary `.db` file, initialize the `Counter` table, return closed `SqliteConnection` instances, and delete `.db`, `-journal`, `-wal`, and `-shm` files with retry during disposal.

- [ ] **Step 3: Implement RepoDb operations**

Use `InsertAsync("Counter", new { Value = value })`, `ExecuteQueryAsync<CounterItem>(...)`, and `ExecuteScalarAsync<long>(...)`. Check cancellation before and after each call. Do not pass a transaction argument.

- [ ] **Step 4: Commit repository infrastructure**

```powershell
git add samples/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb
git commit -m "feat: add RepoDb SQLite repository infrastructure"
```

### Task 3: Add services, contracts, and Minimal API endpoints

**Files:**
- Create: `samples/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb/Contracts/CreateCounterPairRequest.cs`
- Create: `samples/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb/Contracts/TransactionScenarioResponse.cs`
- Create: `samples/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb/Contracts/DiagnosticsResponse.cs`
- Create: `samples/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb/Services/NestedCounterService.cs`
- Create: `samples/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb/Services/CounterApplicationService.cs`
- Create: `samples/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb/Endpoints/CounterEndpoints.cs`

**Interfaces:**
- Produces: `GET /api/counters`.
- Produces: `POST /api/counters/commit`.
- Produces: `POST /api/counters/rollback`.
- Produces: `GET /api/counters/diagnostics`.

- [ ] **Step 1: Implement immutable HTTP contracts**

Use records and default web JSON naming.

- [ ] **Step 2: Implement nested commit and rollback flows**

Complete both scopes in the commit flow. Dispose the inner scope incomplete in the rollback flow, then complete the outer scope so the shared root rolls back.

- [ ] **Step 3: Implement diagnostics**

Verify repository identity, command timeout `15`, pre-canceled `BeginAsync` with no ambient root remaining, and `UnitOfWorkConcurrencyException` from a RepoDb count operation while a reader is open.

- [ ] **Step 4: Map Minimal API endpoints**

Group routes under `/api/counters`, pass `HttpContext.RequestAborted`, and return `Results.Ok(...)`.

- [ ] **Step 5: Commit the application behavior**

```powershell
git add samples/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb
git commit -m "feat: add Minimal API RepoDb transaction scenarios"
```

### Task 4: Configure the host and integration test environment

**Files:**
- Create: `samples/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb/Program.cs`
- Create: `samples/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.http`
- Modify: `tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests/WebApiSampleTests.cs`

**Interfaces:**
- Produces: public partial `Program` for `WebApplicationFactory<Program>`.

- [ ] **Step 1: Initialize RepoDb and register services**

Call `RepoDb.SqliteBootstrap.Initialize()` before creating the application. Register problem details, database ownership, `IUnitOfWorkManager`, repository factory, and services.

- [ ] **Step 2: Configure middleware and endpoints**

Use exception handling, skip HTTPS redirection in `Development` and `Testing`, and call `MapCounterEndpoints()`.

- [ ] **Step 3: Add manual HTTP requests**

Include all four requests in the `.http` file.

- [ ] **Step 4: Verify GREEN**

```powershell
dotnet test tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests.csproj
```

Expected: one passing integration test.

### Task 5: Integrate documentation and solution structure

**Files:**
- Create: `samples/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb/README.md`
- Modify: `README.md`
- Modify: `UnitOfWork.slnx`

**Interfaces:**
- Produces: a discoverable sample with runnable commands, endpoint examples, RepoDb limitations, and verification instructions.

- [ ] **Step 1: Document the sample**

Explain architecture, RepoDb initialization, transaction ownership, cancellation limitation, endpoints, expected results, and concurrency rules.

- [ ] **Step 2: Add both projects to the solution**

Place the sample under `/samples/` and the test project under `/tests/`.

- [ ] **Step 3: Update the root README**

Add the RepoDb sample next to Console and Controllers/Dapper samples and explain when to use each.

- [ ] **Step 4: Run full verification**

```powershell
dotnet restore UnitOfWork.slnx
dotnet build UnitOfWork.slnx --no-restore --warnaserror
dotnet test UnitOfWork.slnx --no-build --logger "console;verbosity=normal"
powershell -ExecutionPolicy Bypass -File scripts/verify-p0-transaction-invariants.ps1
powershell -ExecutionPolicy Bypass -File scripts/verify-p1-async-concurrency-boundary.ps1
```

Expected: all commands pass without warnings or failures.
