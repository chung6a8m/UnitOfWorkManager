using UnitOfWork.Core;
using UnitOfWork.Core.Exceptions;
using UnitOfWork.Tests.Fixtures;
using Xunit;

namespace UnitOfWork.Tests;

public class RepositoryCacheConcurrencyTests
{
    private interface IRepository { }

    private sealed class Repository : IRepository { }

    [Fact]
    public async Task Concurrent_GetRepository_Returns_One_Cached_Instance()
    {
        var factoryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFactory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryCalls = 0;
        var (manager, scopeTask) = Begin((_, _) =>
        {
            if (Interlocked.Increment(ref factoryCalls) == 1)
            {
                factoryStarted.TrySetResult();
                releaseFactory.Task.GetAwaiter().GetResult();
            }

            return new Repository();
        });
        var scope = await scopeTask;

        try
        {
            var first = Task.Run(() => manager.Current.GetRepository<IRepository>());
            await factoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = Task.Run(() =>
            {
                secondRequestStarted.TrySetResult();
                return manager.Current.GetRepository<IRepository>();
            });
            await secondRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            releaseFactory.TrySetResult();

            Assert.Same(await first, await second);
        }
        finally
        {
            releaseFactory.TrySetResult();
        }

        await scope.RollbackAsync();
    }

    [Fact]
    public async Task Concurrent_GetRepository_Invokes_Factory_Exactly_Once()
    {
        var factoryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFactory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryCalls = 0;
        var (manager, scopeTask) = Begin((_, _) =>
        {
            var call = Interlocked.Increment(ref factoryCalls);
            if (call == 1)
            {
                factoryStarted.TrySetResult();
                releaseFactory.Task.GetAwaiter().GetResult();
            }

            return new Repository();
        });
        var scope = await scopeTask;

        try
        {
            var first = Task.Run(() => manager.Current.GetRepository<IRepository>());
            await factoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = Task.Run(() =>
            {
                secondRequestStarted.TrySetResult();
                return manager.Current.GetRepository<IRepository>();
            });
            await secondRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            releaseFactory.TrySetResult();
            await Task.WhenAll(first, second);

            Assert.Equal(1, Volatile.Read(ref factoryCalls));
        }
        finally
        {
            releaseFactory.TrySetResult();
        }

        await scope.RollbackAsync();
    }

    [Fact]
    public async Task Repository_Factory_Failure_Does_Not_Cache_Partial_Instance()
    {
        var factoryCalls = 0;
        Repository? createdBeforeFailure = null;
        var successfulRepository = new Repository();
        var (manager, scopeTask) = Begin((_, _) =>
        {
            if (Interlocked.Increment(ref factoryCalls) == 1)
            {
                createdBeforeFailure = new Repository();
                throw new InvalidOperationException("factory failure");
            }

            return successfulRepository;
        });
        var scope = await scopeTask;

        Assert.Throws<InvalidOperationException>(() => manager.Current.GetRepository<IRepository>());

        Assert.Same(successfulRepository, manager.Current.GetRepository<IRepository>());
        Assert.NotSame(createdBeforeFailure, successfulRepository);
        Assert.Equal(2, Volatile.Read(ref factoryCalls));

        await scope.RollbackAsync();
    }

    [Fact]
    public async Task Repository_Factory_Can_Be_Retried_After_Failure()
    {
        var factoryCalls = 0;
        var (manager, scopeTask) = Begin((_, _) =>
        {
            if (Interlocked.Increment(ref factoryCalls) == 1)
                throw new InvalidOperationException("factory failure");

            return new Repository();
        });
        var scope = await scopeTask;

        Assert.Throws<InvalidOperationException>(() => manager.Current.GetRepository<IRepository>());

        var repository = manager.Current.GetRepository<IRepository>();

        Assert.Same(repository, manager.Current.GetRepository<IRepository>());
        Assert.Equal(2, Volatile.Read(ref factoryCalls));

        await scope.RollbackAsync();
    }

    [Fact]
    public async Task GetRepository_During_Finalization_Is_Rejected()
    {
        var (manager, scopeTask) = Begin((_, _) => new Repository());
        var scope = await scopeTask;

        await scope.CompleteAsync();

        Assert.Throws<UnitOfWorkStateException>(() => scope.GetRepository<IRepository>());
    }

    [Fact]
    public async Task GetRepository_From_Suppressed_Flow_Is_Rejected()
    {
        var connection = new ControlledDbConnection(initiallyOpen: true);
        var manager = new UnitOfWorkManager(
            new ControlledConnectionFactory(connection),
            (_, _) => new Repository());
        using var scope = await manager.BeginAsync();

        var exception = await RunIsolatedAsync(
            () => Record.Exception(() => scope.GetRepository<IRepository>()));

        Assert.IsType<UnitOfWorkConcurrencyException>(exception);
        await scope.RollbackAsync();
    }

    private static (UnitOfWorkManager Manager, Task<IUnitOfWorkScope> ScopeTask) Begin(
        Func<Type, System.Data.Common.DbConnection, object> factory)
    {
        var manager = new UnitOfWorkManager(
            new ControlledConnectionFactory(new ControlledDbConnection(initiallyOpen: true)),
            factory);
        return (manager, manager.BeginAsync());
    }

    private static Task<T> RunIsolatedAsync<T>(Func<T> action)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(action);
        }
    }
}
