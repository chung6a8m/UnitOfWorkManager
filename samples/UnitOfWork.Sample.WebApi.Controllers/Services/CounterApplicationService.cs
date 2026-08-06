using System.Data;
using UnitOfWork.Core;
using UnitOfWork.Core.Exceptions;
using UnitOfWork.Sample.WebApi.Contracts;
using UnitOfWork.Sample.WebApi.Models;
using UnitOfWork.Sample.WebApi.Repositories;

namespace UnitOfWork.Sample.WebApi.Services;

public sealed class CounterApplicationService
{
    private static readonly UnitOfWorkOptions Options = new()
    {
        IsolationLevel = IsolationLevel.Serializable,
        CommandTimeoutSeconds = 15
    };

    private readonly IUnitOfWorkManager _unitOfWorkManager;
    private readonly NestedCounterService _nestedCounterService;

    public CounterApplicationService(
        IUnitOfWorkManager unitOfWorkManager,
        NestedCounterService nestedCounterService)
    {
        _unitOfWorkManager = unitOfWorkManager;
        _nestedCounterService = nestedCounterService;
    }

    public async Task<IReadOnlyList<CounterItem>> ListAsync(
        CancellationToken cancellationToken)
    {
        await using var scope = await _unitOfWorkManager.BeginAsync(
            Options,
            cancellationToken);
        var items = await scope.GetRepository<ICounterRepository>()
            .ListAsync(cancellationToken);
        await scope.CompleteAsync(cancellationToken);
        return items;
    }

    public async Task<TransactionScenarioResponse> CommitPairAsync(
        CreateCounterPairRequest request,
        CancellationToken cancellationToken)
    {
        await using var scope = await _unitOfWorkManager.BeginAsync(
            Options,
            cancellationToken);
        await scope.GetRepository<ICounterRepository>()
            .InsertAsync(request.OuterValue, cancellationToken);
        await _nestedCounterService.InsertAndCompleteAsync(
            request.InnerValue,
            Options,
            cancellationToken);
        await scope.CompleteAsync(cancellationToken);

        var items = await ListAsync(cancellationToken);
        return new TransactionScenarioResponse("commit", true, items);
    }

    public async Task<TransactionScenarioResponse> RollbackPairAsync(
        CreateCounterPairRequest request,
        CancellationToken cancellationToken)
    {
        await using var scope = await _unitOfWorkManager.BeginAsync(
            Options,
            cancellationToken);
        await scope.GetRepository<ICounterRepository>()
            .InsertAsync(request.OuterValue, cancellationToken);
        await _nestedCounterService.InsertWithoutCompletingAsync(
            request.InnerValue,
            Options,
            cancellationToken);
        await scope.CompleteAsync(cancellationToken);

        var items = await ListAsync(cancellationToken);
        return new TransactionScenarioResponse("rollback", false, items);
    }

    public async Task<DiagnosticsResponse> GetDiagnosticsAsync(
        CancellationToken cancellationToken)
    {
        bool repositoryCacheReused;
        int commandTimeoutSeconds;
        var concurrencyGuardObserved = false;

        await using (var scope = await _unitOfWorkManager.BeginAsync(
            Options,
            cancellationToken))
        {
            var firstRepository = scope.GetRepository<ICounterRepository>();
            var secondRepository = scope.GetRepository<ICounterRepository>();
            repositoryCacheReused = ReferenceEquals(
                firstRepository,
                secondRepository);
            commandTimeoutSeconds =
                firstRepository.GetConfiguredCommandTimeoutSeconds();

            await using (var readerCommand = scope.Connection.CreateCommand())
            {
                readerCommand.CommandText = "SELECT Value FROM Counter ORDER BY Id;";
                await using var reader = await readerCommand.ExecuteReaderAsync(
                    cancellationToken);

                try
                {
                    await firstRepository.CountAsync(cancellationToken);
                }
                catch (UnitOfWorkConcurrencyException)
                {
                    concurrencyGuardObserved = true;
                }
            }

            await scope.CompleteAsync(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var cancellationObserved = false;
        using var canceledTokenSource = new CancellationTokenSource();
        canceledTokenSource.Cancel();

        try
        {
            await using var unexpectedScope = await _unitOfWorkManager.BeginAsync(
                Options,
                canceledTokenSource.Token);
        }
        catch (OperationCanceledException)
            when (canceledTokenSource.IsCancellationRequested)
        {
            cancellationObserved = true;
        }

        return new DiagnosticsResponse(
            repositoryCacheReused,
            commandTimeoutSeconds,
            cancellationObserved,
            !_unitOfWorkManager.HasCurrent,
            concurrencyGuardObserved);
    }
}
