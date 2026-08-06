using UnitOfWork.Core;
using UnitOfWork.Sample.ConsoleApp.Repositories;

namespace UnitOfWork.Sample.ConsoleApp.Services;

public sealed class CounterService
{
    private readonly IUnitOfWorkManager _unitOfWorkManager;

    public CounterService(IUnitOfWorkManager unitOfWorkManager)
    {
        _unitOfWorkManager = unitOfWorkManager;
    }

    public async Task<(bool RepositoryCacheReused, int CommandTimeoutSeconds)>
        CommitNestedAsync(
            UnitOfWorkOptions options,
            CancellationToken cancellationToken = default)
    {
        await using var outerScope =
            await _unitOfWorkManager.BeginAsync(options, cancellationToken);

        var firstRepository = outerScope.GetRepository<ICounterRepository>();
        var secondRepository = outerScope.GetRepository<ICounterRepository>();

        await firstRepository.InsertAsync(10, cancellationToken);
        await InsertAndCompleteInnerAsync(20, options, cancellationToken);

        var timeoutSeconds = firstRepository.GetConfiguredCommandTimeoutSeconds();
        await outerScope.CompleteAsync(cancellationToken);

        return (ReferenceEquals(firstRepository, secondRepository), timeoutSeconds);
    }

    public async Task RollbackBecauseInnerIsIncompleteAsync(
        UnitOfWorkOptions options,
        CancellationToken cancellationToken = default)
    {
        await using var outerScope =
            await _unitOfWorkManager.BeginAsync(options, cancellationToken);

        var repository = outerScope.GetRepository<ICounterRepository>();
        await repository.InsertAsync(30, cancellationToken);

        await InsertWithoutCompletingInnerAsync(40, options, cancellationToken);

        // This completes the outer lease, but the incomplete inner lease has already
        // requested rollback for the shared root transaction.
        await outerScope.CompleteAsync(cancellationToken);
    }

    private async Task InsertAndCompleteInnerAsync(
        int value,
        UnitOfWorkOptions options,
        CancellationToken cancellationToken)
    {
        await using var innerScope =
            await _unitOfWorkManager.BeginAsync(options, cancellationToken);

        await innerScope
            .GetRepository<ICounterRepository>()
            .InsertAsync(value, cancellationToken);

        await innerScope.CompleteAsync(cancellationToken);
    }

    private async Task InsertWithoutCompletingInnerAsync(
        int value,
        UnitOfWorkOptions options,
        CancellationToken cancellationToken)
    {
        await using var innerScope =
            await _unitOfWorkManager.BeginAsync(options, cancellationToken);

        await innerScope
            .GetRepository<ICounterRepository>()
            .InsertAsync(value, cancellationToken);

        // Intentionally omit CompleteAsync(). DisposeAsync() marks the shared root
        // transaction for rollback while leaving outer resource ownership intact.
    }
}
