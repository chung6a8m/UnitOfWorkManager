using UnitOfWork.Core;
using UnitOfWork.Tests.Fixtures;
using Xunit;
using CoreUoW = UnitOfWork.Core.UnitOfWork;

namespace UnitOfWork.Tests;

public sealed class UnitOfWorkManagerAmbientTests : UnitOfWorkTestBase
{
    private sealed class RepositoryMarker { }

    private static UnitOfWorkManager CreateManager(IDbConnectionFactory factory) =>
        new(factory, (type, _, _) =>
            type == typeof(RepositoryMarker)
                ? new RepositoryMarker()
                : throw new NotSupportedException());

    [Fact]
    public async Task BeginAsync_Publishes_Current_And_Flow_To_Caller()
    {
        var connection = new ControlledDbConnection(initiallyOpen: true);
        var manager = CreateManager(new ControlledConnectionFactory(connection));

        var uow = await manager.BeginAsync();

        Assert.True(manager.HasCurrent);
        Assert.Same(uow, manager.Current);
        Assert.NotNull(uow.GetRepository<RepositoryMarker>());

        await uow.RollbackAsync();
        uow.Dispose();
        manager.ClearCurrent();
    }

    [Fact]
    public async Task Nested_Begin_During_Initialization_Awaits_The_Same_UnitOfWork()
    {
        var connection = new ControlledDbConnection();
        var factory = new ControlledConnectionFactory(connection);
        var manager = CreateManager(factory);

        var firstBegin = manager.BeginAsync();
        var nestedBegin = manager.BeginAsync();

        try
        {
            Assert.Equal(1, factory.CreateCount);
            Assert.False(firstBegin.IsCompleted);
            Assert.False(nestedBegin.IsCompleted);

            connection.ReleaseOpen();
            var results = await Task.WhenAll(firstBegin, nestedBegin);

            Assert.Same(results[0], results[1]);
            await results[1].RollbackAsync();
            await results[0].RollbackAsync();
        }
        finally
        {
            connection.ReleaseOpen();
            var completed = await Task.WhenAll(firstBegin, nestedBegin);
            foreach (var uow in completed.Distinct())
                uow.Dispose();
            manager.ClearCurrent();
        }
    }

    [Fact]
    public async Task Reentrant_Begin_During_OpenAsync_Waits_For_Initialization()
    {
        var connection = new ControlledDbConnection();
        var manager = CreateManager(new ControlledConnectionFactory(connection));
        Task<IUnitOfWork>? reentrantBegin = null;
        connection.Opening = () => reentrantBegin = manager.BeginAsync();

        var firstBegin = manager.BeginAsync();

        try
        {
            Assert.NotNull(reentrantBegin);
            Assert.False(reentrantBegin!.IsCompleted);

            connection.ReleaseOpen();
            var results = await Task.WhenAll(firstBegin, reentrantBegin);

            Assert.Same(results[0], results[1]);
            await results[1].RollbackAsync();
            await results[0].RollbackAsync();
        }
        finally
        {
            connection.ReleaseOpen();
            if (reentrantBegin is not null)
            {
                var completed = await Task.WhenAll(firstBegin, reentrantBegin);
                foreach (var uow in completed.Distinct())
                    uow.Dispose();
            }
            manager.ClearCurrent();
        }
    }

    [Fact]
    public async Task Failed_Transaction_Initialization_Disposes_And_Clears_Ambient_State()
    {
        var expected = new InvalidOperationException("transaction initialization failed");
        var failingConnection = new ControlledDbConnection(beginTransactionException: expected);
        var succeedingConnection = new ControlledDbConnection(initiallyOpen: true);
        var manager = CreateManager(new ControlledConnectionFactory(
            failingConnection,
            succeedingConnection));

        var failedBegin = manager.BeginAsync();
        Assert.True(manager.HasCurrent);
        Assert.False(failedBegin.IsCompleted);

        failingConnection.ReleaseOpen();
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => failedBegin);

        Assert.Same(expected, actual);
        Assert.True(failingConnection.IsDisposed);
        Assert.False(manager.HasCurrent);
        Assert.Throws<InvalidOperationException>(() => manager.Current);

        var next = await manager.BeginAsync();
        Assert.NotNull(next.GetRepository<RepositoryMarker>());

        await next.RollbackAsync();
        next.Dispose();
        manager.ClearCurrent();
    }

    [Fact]
    public async Task Initialization_Error_Is_Preserved_When_Cleanup_Also_Fails()
    {
        var initializationError = new InvalidOperationException("initialization failed");
        var cleanupError = new IOException("cleanup failed");
        var connection = new ControlledDbConnection(
            initiallyOpen: true,
            beginTransactionException: initializationError,
            disposeException: cleanupError);
        var manager = CreateManager(new ControlledConnectionFactory(connection));

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(manager.BeginAsync);

        Assert.Same(initializationError, actual);
        Assert.Same(cleanupError, actual.Data["UnitOfWorkCleanupException"]);
        Assert.True(connection.IsDisposed);
        Assert.False(manager.HasCurrent);
        Assert.Throws<InvalidOperationException>(() => manager.Current);
        Assert.Null(CoreUoW.AmbientFlowId);
    }

    [Fact]
    public async Task Dispose_Attempts_Connection_When_Transaction_Disposal_Fails()
    {
        var transactionCleanupError = new IOException("transaction cleanup failed");
        var connection = new ControlledDbConnection(
            initiallyOpen: true,
            transactionDisposeException: transactionCleanupError);
        var uow = new CoreUoW(connection, (type, _, _) =>
            type == typeof(RepositoryMarker)
                ? new RepositoryMarker()
                : throw new NotSupportedException());
        await uow.BeginTransactionAsync();

        var actual = Assert.Throws<IOException>(uow.Dispose);

        Assert.Same(transactionCleanupError, actual);
        Assert.True(connection.IsDisposed);
        Assert.Null(CoreUoW.AmbientFlowId);
    }

    [Fact]
    public async Task Child_Flow_With_Its_Own_UnitOfWork_Does_Not_Clear_Parent_Flow()
    {
        var parentConnection = new ControlledDbConnection(initiallyOpen: true);
        var childConnection = new ControlledDbConnection(initiallyOpen: true);
        var parent = new CoreUoW(parentConnection, (type, _, _) =>
            type == typeof(RepositoryMarker)
                ? new RepositoryMarker()
                : throw new NotSupportedException());
        await parent.BeginTransactionAsync();

        try
        {
            await Task.Run(async () =>
            {
                var child = new CoreUoW(childConnection, (type, _, _) =>
                    type == typeof(RepositoryMarker)
                        ? new RepositoryMarker()
                        : throw new NotSupportedException());
                await child.BeginTransactionAsync();
                await child.RollbackAsync();
                child.Dispose();
            });

            Assert.NotNull(parent.GetRepository<RepositoryMarker>());
            await parent.RollbackAsync();
        }
        finally
        {
            parent.Dispose();
        }
    }
}
