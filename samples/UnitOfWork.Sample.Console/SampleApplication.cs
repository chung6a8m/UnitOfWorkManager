using System.Data;
using System.Data.Common;
using UnitOfWork.Core;
using UnitOfWork.Core.Exceptions;
using UnitOfWork.Sample.ConsoleApp.Infrastructure;
using UnitOfWork.Sample.ConsoleApp.Repositories;
using UnitOfWork.Sample.ConsoleApp.Services;

namespace UnitOfWork.Sample.ConsoleApp;

public static class SampleApplication
{
    private const int ExpectedCommandTimeoutSeconds = 15;

    public static async Task<SampleSummary> RunAsync(
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);

        await using var database = new SqliteSampleDatabase();
        var manager = new UnitOfWorkManager(database, CreateRepository);
        var service = new CounterService(manager);
        var options = new UnitOfWorkOptions
        {
            IsolationLevel = IsolationLevel.Serializable,
            CommandTimeoutSeconds = ExpectedCommandTimeoutSeconds
        };

        var (repositoryCacheReused, commandTimeoutSeconds) =
            await service.CommitNestedAsync(options, cancellationToken);
        var valuesAfterCommit = await database.ReadValuesAsync(cancellationToken);
        var commitPersisted = valuesAfterCommit.SequenceEqual(new[] { 10, 20 });

        await WriteResultAsync(
            output,
            "Nested commit",
            commitPersisted,
            $"Committed values: {FormatValues(valuesAfterCommit)}");
        await WriteResultAsync(
            output,
            "Repository cache",
            repositoryCacheReused,
            "Two GetRepository<T>() calls returned the same root-scoped instance.");
        await WriteResultAsync(
            output,
            "Command timeout",
            commandTimeoutSeconds == ExpectedCommandTimeoutSeconds,
            $"Observed {commandTimeoutSeconds} seconds.");

        await service.RollbackBecauseInnerIsIncompleteAsync(
            options,
            cancellationToken);
        var valuesAfterRollback = await database.ReadValuesAsync(cancellationToken);
        var nestedIncompleteRolledBack =
            valuesAfterRollback.SequenceEqual(valuesAfterCommit);

        await WriteResultAsync(
            output,
            "Incomplete nested scope",
            nestedIncompleteRolledBack,
            $"Committed values remain: {FormatValues(valuesAfterRollback)}");

        var cancellationObserved = await DemonstrateCancellationAsync(
            manager,
            options,
            cancellationToken);
        await WriteResultAsync(
            output,
            "Cancellation",
            cancellationObserved,
            "A pre-canceled BeginAsync token was observed and no ambient root remained.");

        var concurrencyGuardObserved = await DemonstrateConcurrencyGuardAsync(
            manager,
            options,
            cancellationToken);
        await WriteResultAsync(
            output,
            "Reader concurrency guard",
            concurrencyGuardObserved,
            "A second command was rejected while a reader held the operation lease.");

        var finalValues = await database.ReadValuesAsync(cancellationToken);
        var summary = new SampleSummary(
            commitPersisted,
            nestedIncompleteRolledBack,
            repositoryCacheReused,
            commandTimeoutSeconds,
            cancellationObserved,
            concurrencyGuardObserved,
            finalValues);

        await output.WriteLineAsync(
            summary.AllPassed
                ? "All sample scenarios passed."
                : "One or more sample scenarios failed.");

        return summary;
    }

    private static object CreateRepository(Type repositoryType, DbConnection connection)
    {
        if (repositoryType == typeof(ICounterRepository))
            return new CounterRepository(connection);

        throw new NotSupportedException(
            $"Repository is not registered: {repositoryType.FullName}");
    }

    private static async Task<bool> DemonstrateCancellationAsync(
        IUnitOfWorkManager manager,
        UnitOfWorkOptions options,
        CancellationToken cancellationToken)
    {
        using var canceledTokenSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        canceledTokenSource.Cancel();

        try
        {
            await using var unexpectedScope =
                await manager.BeginAsync(options, canceledTokenSource.Token);
            return false;
        }
        catch (OperationCanceledException)
            when (canceledTokenSource.IsCancellationRequested)
        {
            return !manager.HasCurrent;
        }
    }

    private static async Task<bool> DemonstrateConcurrencyGuardAsync(
        IUnitOfWorkManager manager,
        UnitOfWorkOptions options,
        CancellationToken cancellationToken)
    {
        await using var scope = await manager.BeginAsync(options, cancellationToken);
        var concurrencyRejected = false;

        await using (var readerCommand = scope.Connection.CreateCommand())
        {
            readerCommand.CommandText = "SELECT Value FROM Counter ORDER BY Id;";

            await using var reader =
                await readerCommand.ExecuteReaderAsync(cancellationToken);
            await using var overlappingCommand = scope.Connection.CreateCommand();
            overlappingCommand.CommandText = "SELECT COUNT(*) FROM Counter;";

            try
            {
                await overlappingCommand.ExecuteScalarAsync(cancellationToken);
            }
            catch (UnitOfWorkConcurrencyException)
            {
                concurrencyRejected = true;
            }
        }

        await scope.CompleteAsync(cancellationToken);
        return concurrencyRejected;
    }

    private static Task WriteResultAsync(
        TextWriter output,
        string scenario,
        bool passed,
        string details) =>
        output.WriteLineAsync($"[{(passed ? "PASS" : "FAIL")}] {scenario}: {details}");

    private static string FormatValues(IEnumerable<int> values) =>
        $"[{string.Join(", ", values)}]";
}
