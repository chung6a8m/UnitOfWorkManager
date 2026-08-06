# Console SQLite Sample Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a runnable and tested .NET 8 console sample that demonstrates `UnitOfWork.Core` with a real SQLite database.

**Architecture:** The executable sample separates temporary database ownership, repository access, nested services, and scenario orchestration. Existing xUnit tests reference the executable assembly and invoke the scenario runner directly.

**Tech Stack:** .NET 8, C# latest, `UnitOfWork.Core`, `Microsoft.Data.Sqlite` 8.0.10, xUnit 2.9.2.

## Global Constraints

- Use `await using` for Unit of Work scopes and async ADO.NET resources.
- Do not assign `DbCommand.Transaction` manually in sample repository code.
- Use a file-based SQLite database with `Pooling=False`.
- Keep the final committed values exactly `10` and `20`.
- Use deterministic reader-lifetime concurrency rejection rather than timing delays.
- Preserve the repository encoding and line-ending conventions.

---

### Task 1: Add the failing sample integration test

**Files:**
- Create: `tests/UnitOfWork.Tests/ConsoleSampleTests.cs`
- Modify: `tests/UnitOfWork.Tests/UnitOfWork.Tests.csproj`

**Interfaces:**
- Consumes: `SampleApplication.RunAsync(TextWriter, CancellationToken)` and `SampleSummary` from Task 2.
- Produces: An xUnit test that validates all six scenarios and the final database values.

- [ ] **Step 1: Add a project reference to the sample executable**

Add this reference to `UnitOfWork.Tests.csproj`:

```xml
<ProjectReference Include="..\..\samples\UnitOfWork.Sample.Console\UnitOfWork.Sample.Console.csproj" />
```

- [ ] **Step 2: Write the failing test**

```csharp
[Fact]
public async Task RunAsync_Demonstrates_All_Supported_Scenarios()
{
    using var output = new StringWriter();

    var summary = await SampleApplication.RunAsync(output);

    Assert.True(summary.AllPassed, output.ToString());
    Assert.Equal(new[] { 10, 20 }, summary.FinalValues);
    Assert.Contains("All sample scenarios passed.", output.ToString());
}
```

- [ ] **Step 3: Verify the test fails because the sample project and API do not exist**

Run:

```powershell
dotnet test tests/UnitOfWork.Tests/UnitOfWork.Tests.csproj --filter ConsoleSampleTests
```

Expected: build failure identifying the missing sample project or `SampleApplication` API.

### Task 2: Implement the SQLite console sample

**Files:**
- Create: `samples/UnitOfWork.Sample.Console/UnitOfWork.Sample.Console.csproj`
- Create: `samples/UnitOfWork.Sample.Console/Program.cs`
- Create: `samples/UnitOfWork.Sample.Console/SampleApplication.cs`
- Create: `samples/UnitOfWork.Sample.Console/SampleSummary.cs`
- Create: `samples/UnitOfWork.Sample.Console/Infrastructure/SqliteSampleDatabase.cs`
- Create: `samples/UnitOfWork.Sample.Console/Repositories/ICounterRepository.cs`
- Create: `samples/UnitOfWork.Sample.Console/Repositories/CounterRepository.cs`
- Create: `samples/UnitOfWork.Sample.Console/Services/CounterService.cs`
- Create: `samples/UnitOfWork.Sample.Console/README.md`

**Interfaces:**
- Produces: `public static Task<SampleSummary> SampleApplication.RunAsync(TextWriter output, CancellationToken cancellationToken = default)`.
- Produces: `public sealed record SampleSummary` with scenario result properties and `AllPassed`.

- [ ] **Step 1: Add the executable project**

Target `net8.0`, enable nullable and implicit usings, reference `UnitOfWork.Core`, and use `Microsoft.Data.Sqlite` version `8.0.10`.

- [ ] **Step 2: Implement the temporary SQLite database owner**

Create table `Counter (Id INTEGER PRIMARY KEY AUTOINCREMENT, Value INTEGER NOT NULL)`, return closed `SqliteConnection` instances from `IDbConnectionFactory.CreateConnection()`, read committed values through an independent connection, and delete database sidecars with retry during async disposal.

- [ ] **Step 3: Implement the repository**

`CounterRepository.InsertAsync` creates a parameterized command and executes it asynchronously. `GetConfiguredCommandTimeoutSeconds` creates a command and returns its timeout without manually assigning a transaction.

- [ ] **Step 4: Implement nested services**

The commit flow inserts `10` in the outer scope and `20` in a completed nested scope. The rollback flow inserts `30` in the outer scope and `40` in a nested scope that is disposed without completion.

- [ ] **Step 5: Implement scenario orchestration**

Use `CommandTimeoutSeconds = 15` and SQLite-supported `IsolationLevel.Serializable`. Verify repository cache identity, cancellation, reader-lifetime concurrency rejection, and final committed values. Write one PASS/FAIL line per scenario.

- [ ] **Step 6: Implement the executable entry point and sample README**

Run `SampleApplication`, set exit code `0` only when `AllPassed` is true, and document commands plus expected output.

- [ ] **Step 7: Verify the focused test passes**

Run:

```powershell
dotnet test tests/UnitOfWork.Tests/UnitOfWork.Tests.csproj --filter ConsoleSampleTests
```

Expected: one passing test.

### Task 3: Integrate the sample into the repository

**Files:**
- Modify: `UnitOfWork.slnx`
- Modify: `README.md`

**Interfaces:**
- Produces: A discoverable `/samples/` solution folder and root documentation link.

- [ ] **Step 1: Add the sample project to the solution**

```xml
<Folder Name="/samples/">
  <Project Path="samples/UnitOfWork.Sample.Console/UnitOfWork.Sample.Console.csproj" />
</Folder>
```

- [ ] **Step 2: Add a root README sample section**

Document the six scenarios and the `dotnet run --project ...` command, and update the repository structure tree.

- [ ] **Step 3: Run full verification**

```powershell
dotnet restore UnitOfWork.slnx
dotnet build UnitOfWork.slnx --no-restore --warnaserror
dotnet test UnitOfWork.slnx --no-build --logger "console;verbosity=normal"
powershell -ExecutionPolicy Bypass -File scripts/verify-p0-transaction-invariants.ps1
powershell -ExecutionPolicy Bypass -File scripts/verify-p1-async-concurrency-boundary.ps1
```

Expected: all commands pass without warnings or failures.
