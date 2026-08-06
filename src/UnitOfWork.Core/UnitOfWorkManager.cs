using System.Data.Common;
using UnitOfWork.Core.Exceptions;

namespace UnitOfWork.Core;

public class UnitOfWorkManager : IUnitOfWorkManager
{
    private static readonly UnitOfWorkOptions DefaultOptions = new();

    private sealed class AmbientUnitOfWorkHolder
    {
        public RootUnitOfWork? Root;
        public Task? Initialization;
    }

    private readonly AsyncLocal<AmbientUnitOfWorkHolder?> _current = new();
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly Func<Type, DbConnection, object> _repositoryFactory;
    private readonly IUnitOfWorkTransactionFactory _transactionFactory;

    public UnitOfWorkManager(
        IDbConnectionFactory connectionFactory,
        Func<Type, DbConnection, object> repositoryFactory,
        IUnitOfWorkTransactionFactory? transactionFactory = null)
    {
        _connectionFactory = connectionFactory;
        _repositoryFactory = repositoryFactory;
        _transactionFactory = transactionFactory ?? new DefaultUnitOfWorkTransactionFactory();
    }

    public bool HasCurrent => _current.Value?.Root is not null;

    public IUnitOfWorkContext Current => _current.Value?.Root
        ?? throw new InvalidOperationException("No unit of work has been started.");

    public Task<IUnitOfWorkScope> BeginAsync(
        UnitOfWorkOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<IUnitOfWorkScope>(cancellationToken);

        UnitOfWorkOptions normalizedOptions;
        try
        {
            normalizedOptions = (options ?? DefaultOptions).Validate();
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return Task.FromException<IUnitOfWorkScope>(exception);
        }

        var currentHolder = _current.Value;
        if (currentHolder?.Root is { } currentRoot)
        {
            if (currentRoot.Options != normalizedOptions)
            {
                return Task.FromException<IUnitOfWorkScope>(new UnitOfWorkStateException(
                    "Nested unit of work options must match the active root options."));
            }

            var initialization = currentHolder.Initialization
                ?? throw new InvalidOperationException("The ambient unit of work has no initialization task.");
            return AwaitScopeAsync(
                currentRoot.AcquireScope(),
                currentRoot,
                initialization,
                cancellationToken);
        }

        var holder = new AmbientUnitOfWorkHolder();
        var root = CreateRoot(holder, normalizedOptions);
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        holder.Root = root;
        holder.Initialization = completion.Task;
        _current.Value = holder;

        var scope = root.AcquireScope();
        _ = InitializeRootAsync(root, holder, completion);
        return AwaitScopeAsync(scope, root, completion.Task, cancellationToken);
    }

    private RootUnitOfWork CreateRoot(
        AmbientUnitOfWorkHolder holder,
        UnitOfWorkOptions options)
    {
        RootUnitOfWork root = null!;
        root = new RootUnitOfWork(
            _connectionFactory.CreateConnection(),
            _repositoryFactory,
            () => ReferenceEquals(_current.Value?.Root, root),
            () => ClearRoot(holder, root),
            options,
            _transactionFactory);
        return root;
    }

    private static async Task InitializeRootAsync(
        RootUnitOfWork root,
        AmbientUnitOfWorkHolder holder,
        TaskCompletionSource completion)
    {
        try
        {
            await root.InitializeAsync().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception initializationError)
        {
            if (ReferenceEquals(holder.Root, root))
            {
                holder.Root = null;
                holder.Initialization = null;
            }

            completion.TrySetException(initializationError);
        }
    }

    private static async Task<IUnitOfWorkScope> AwaitScopeAsync(
        UnitOfWorkScope scope,
        RootUnitOfWork root,
        Task initialization,
        CancellationToken cancellationToken)
    {
        try
        {
            await initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
            scope.Activate();
            return scope;
        }
        catch (Exception error)
        {
            root.CancelScopeBeforeActivation(scope);

            if (error is OperationCanceledException &&
                cancellationToken.IsCancellationRequested &&
                root.InitializationCancellationRequested)
            {
                try
                {
                    await initialization.ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the caller's cancellation after root cleanup completes.
                }
            }

            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            throw;
        }
    }

    private void ClearRoot(AmbientUnitOfWorkHolder holder, RootUnitOfWork root)
    {
        if (!ReferenceEquals(holder.Root, root))
            return;

        holder.Root = null;
        holder.Initialization = null;

        if (ReferenceEquals(_current.Value, holder))
            _current.Value = null;
    }
}
