# ASP.NET Core Controllers Dapper SQLite Sample Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a runnable and integration-tested .NET 8 Controllers-based Web API sample using Dapper, SQLite, and `UnitOfWork.Core`.

**Architecture:** A Web API project separates controller, application-service, nested-service, repository, database ownership, and transport contracts. A dedicated test project hosts the application with `WebApplicationFactory<Program>` and verifies the HTTP behavior end to end.

**Tech Stack:** .NET 8, ASP.NET Core Controllers, Dapper 2.1.79, Microsoft.Data.Sqlite 8.0.10, Microsoft.AspNetCore.Mvc.Testing 8.0.29, xUnit 2.9.2.

## Global Constraints

- Use MVC controllers, not Minimal API endpoint mappings.
- Use built-in ASP.NET Core dependency injection.
- Pass the controller cancellation token through services, Unit of Work, and Dapper `CommandDefinition`.
- Never pass a separate transaction to Dapper and never assign `DbCommand.Transaction` manually.
- Use `await using` for Unit of Work scopes and asynchronous ADO.NET resources.
- Use a file-based SQLite database with `Pooling=False`.
- Keep the successful committed values exactly `10` and `20`; rolled-back values `30` and `40` must never appear.
- Keep the Web API sample independent from the existing Console sample.

---

### Task 1: Specify the HTTP behavior with a failing integration test

**Files:**
- Create: `tests/UnitOfWork.Sample.WebApi.Tests/UnitOfWork.Sample.WebApi.Tests.csproj`
- Create: `tests/UnitOfWork.Sample.WebApi.Tests/WebApiSampleTests.cs`

**Interfaces:**
- Consumes: `Program` from the Web API project and JSON contracts from the HTTP endpoints.
- Produces: One sequential end-to-end test covering list, commit, rollback, and diagnostics.

- [ ] **Step 1: Create the test project**

Use `Microsoft.NET.Test.Sdk` 17.11.1, xUnit 2.9.2, xUnit runner 2.8.2, `Microsoft.AspNetCore.Mvc.Testing` 8.0.29, and a project reference to the new Web API project.

- [ ] **Step 2: Write the failing test**

The test must:

```csharp
using var factory = new WebApplicationFactory<Program>();
using var client = factory.CreateClient();

Assert.Empty(await client.GetFromJsonAsync<CounterItem[]>("/api/counters"));

var commit = await client.PostAsJsonAsync(
    "/api/counters/commit",
    new { outerValue = 10, innerValue = 20 });
commit.EnsureSuccessStatusCode();

var rollback = await client.PostAsJsonAsync(
    "/api/counters/rollback",
    new { outerValue = 30, innerValue = 40 });
rollback.EnsureSuccessStatusCode();

var values = await client.GetFromJsonAsync<CounterItem[]>("/api/counters");
Assert.Equal(new[] { 10, 20 }, values!.Select(item => item.Value));

var diagnostics = await client.GetFromJsonAsync<DiagnosticsResponse>(
    "/api/counters/diagnostics");
Assert.True(diagnostics!.AllPassed);
```

- [ ] **Step 3: Verify RED**

Run:

```powershell
dotnet test tests/UnitOfWork.Sample.WebApi.Tests/UnitOfWork.Sample.WebApi.Tests.csproj
```

Expected: build failure because the referenced Web API project and endpoint contracts do not exist.

### Task 2: Implement SQLite ownership and the Dapper repository

**Files:**
- Create: `samples/UnitOfWork.Sample.WebApi.Controllers/UnitOfWork.Sample.WebApi.Controllers.csproj`
- Create: `samples/UnitOfWork.Sample.WebApi.Controllers/Infrastructure/SqliteSampleDatabase.cs`
- Create: `samples/UnitOfWork.Sample.WebApi.Controllers/Repositories/ICounterRepository.cs`
- Create: `samples/UnitOfWork.Sample.WebApi.Controllers/Repositories/DapperCounterRepository.cs`
- Create: `samples/UnitOfWork.Sample.WebApi.Controllers/Models/CounterItem.cs`

**Interfaces:**
- Produces: `SqliteSampleDatabase : IDbConnectionFactory, IAsyncDisposable`.
- Produces: `ICounterRepository.InsertAsync`, `ListAsync`, `CountAsync`, and `GetConfiguredCommandTimeoutSeconds`.

- [ ] **Step 1: Create the Web SDK project**

Reference `UnitOfWork.Core`, Dapper 2.1.79, and Microsoft.Data.Sqlite 8.0.10.

- [ ] **Step 2: Implement database ownership**

Create a unique temporary `.db` file, initialize the `Counter` table, return closed `SqliteConnection` instances, and delete `.db`, `-journal`, `-wal`, and `-shm` files with retry during async disposal.

- [ ] **Step 3: Implement the Dapper repository**

Use parameterized SQL and `CommandDefinition` cancellation tokens. Do not provide Dapper's `transaction` argument. Commands receive the Unit of Work timeout through the transaction-bound connection.

### Task 3: Implement nested services and controller contracts

**Files:**
- Create: `samples/UnitOfWork.Sample.WebApi.Controllers/Contracts/CreateCounterPairRequest.cs`
- Create: `samples/UnitOfWork.Sample.WebApi.Controllers/Contracts/TransactionScenarioResponse.cs`
- Create: `samples/UnitOfWork.Sample.WebApi.Controllers/Contracts/DiagnosticsResponse.cs`
- Create: `samples/UnitOfWork.Sample.WebApi.Controllers/Services/NestedCounterService.cs`
- Create: `samples/UnitOfWork.Sample.WebApi.Controllers/Services/CounterApplicationService.cs`
- Create: `samples/UnitOfWork.Sample.WebApi.Controllers/Controllers/CountersController.cs`

**Interfaces:**
- Produces: `GET /api/counters`.
- Produces: `POST /api/counters/commit`.
- Produces: `POST /api/counters/rollback`.
- Produces: `GET /api/counters/diagnostics`.

- [ ] **Step 1: Implement the transport contracts**

Use immutable records with exact JSON property names derived from their C# property names under the default web JSON policy.

- [ ] **Step 2: Implement nested transaction scenarios**

The commit flow completes both nested scopes. The rollback flow disposes the inner scope incomplete and completes the outer scope, producing a root rollback.

- [ ] **Step 3: Implement diagnostics**

Within controlled scopes verify repository identity, configured timeout `15`, a pre-canceled `BeginAsync` with no remaining ambient root, and rejection of a Dapper count command while a streaming reader is open.

- [ ] **Step 4: Implement the controller**

Pass the action `CancellationToken` directly to every service method and return typed `ActionResult` responses.

### Task 4: Configure the host and make integration testing possible

**Files:**
- Create: `samples/UnitOfWork.Sample.WebApi.Controllers/Program.cs`
- Create: `samples/UnitOfWork.Sample.WebApi.Controllers/UnitOfWork.Sample.WebApi.Controllers.http`

**Interfaces:**
- Produces: public partial `Program` for `WebApplicationFactory<Program>`.
- Consumes: database, repository factory, and application services from Tasks 2 and 3.

- [ ] **Step 1: Configure services**

Register controllers, problem details, the database singleton, singleton `IUnitOfWorkManager`, and scoped services.

- [ ] **Step 2: Configure middleware**

Use exception handling, HTTPS redirection only outside the `Testing` environment, and map controllers.

- [ ] **Step 3: Add manual HTTP requests**

Include list, commit, rollback, and diagnostics requests in the `.http` file.

- [ ] **Step 4: Verify GREEN**

Run the focused integration test and expect one passing test.

### Task 5: Integrate documentation and solution structure

**Files:**
- Create: `samples/UnitOfWork.Sample.WebApi.Controllers/README.md`
- Modify: `README.md`
- Modify: `UnitOfWork.slnx`

**Interfaces:**
- Produces: discoverable sample and test projects with runnable commands and expected responses.

- [ ] **Step 1: Document the sample**

Explain the architecture, endpoints, Dapper transaction rule, cancellation flow, run command, and example calls.

- [ ] **Step 2: Add projects to the solution**

Add the Web API project under `/samples/` and the integration test project under `/tests/`.

- [ ] **Step 3: Update the root README**

Link the new sample next to the existing Console sample and summarize when to use each.

- [ ] **Step 4: Run full verification**

```powershell
dotnet restore UnitOfWork.slnx
dotnet build UnitOfWork.slnx --no-restore --warnaserror
dotnet test UnitOfWork.slnx --no-build --logger "console;verbosity=normal"
powershell -ExecutionPolicy Bypass -File scripts/verify-p0-transaction-invariants.ps1
powershell -ExecutionPolicy Bypass -File scripts/verify-p1-async-concurrency-boundary.ps1
```

Expected: all commands pass without warnings or failures.
