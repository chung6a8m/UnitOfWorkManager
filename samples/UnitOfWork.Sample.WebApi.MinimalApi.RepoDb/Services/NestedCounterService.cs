using UnitOfWork.Core;
using UnitOfWork.Sample.WebApi.MinimalApi.Repositories;

namespace UnitOfWork.Sample.WebApi.MinimalApi.Services;

public sealed class NestedCounterService
{
    private readonly IUnitOfWorkManager _unitOfWorkManager;

    public NestedCounterService(IUnitOfWorkManager unitOfWorkManager)
    {
        _unitOfWorkManager = unitOfWorkManager;
    }

    public async Task InsertAndCompleteAsync(
        int value,
        UnitOfWorkOptions options,
        CancellationToken cancellationToken)
    {
        await using var scope = await _unitOfWorkManager.BeginAsync(
            options,
            cancellationToken);
        await scope.GetRepository<ICounterRepository>()
            .InsertAsync(value, cancellationToken);
        await scope.CompleteAsync(cancellationToken);
    }

    public async Task InsertWithoutCompletingAsync(
        int value,
        UnitOfWorkOptions options,
        CancellationToken cancellationToken)
    {
        await using var scope = await _unitOfWorkManager.BeginAsync(
            options,
            cancellationToken);
        await scope.GetRepository<ICounterRepository>()
            .InsertAsync(value, cancellationToken);
    }
}
