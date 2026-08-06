using UnitOfWork.RepoDb.ProviderTests.Shared;

namespace UnitOfWork.RepoDb.MySql.Tests;

public sealed class ProviderContractTests
{
    [ExternalDatabaseFact("UOW_TEST_MYSQL")]
    public async Task Rpm01_Cold_Cache_Insert() => await RunAsync(RepoDbProviderContract.ColdCacheInsertAsync);

    [ExternalDatabaseFact("UOW_TEST_MYSQL")]
    public async Task Rpm02_Hot_Cache_Insert() => await RunAsync(RepoDbProviderContract.HotCacheInsertAsync);

    [ExternalDatabaseFact("UOW_TEST_MYSQL")]
    public async Task Rpm03_Two_Table_Discovery() => await RunAsync(RepoDbProviderContract.TwoTablesDiscoveryAsync);

    [ExternalDatabaseFact("UOW_TEST_MYSQL")]
    public async Task Rpm04_Identity_Insert() => await RunAsync(RepoDbProviderContract.IdentityInsertAsync);

    [ExternalDatabaseFact("UOW_TEST_MYSQL")]
    public async Task Rpm05_Cancelled_Discovery_Releases_Lease() => await RunAsync(RepoDbProviderContract.CancelledDiscoveryReleasesLeaseAsync);

    [ExternalDatabaseFact("UOW_TEST_MYSQL")]
    public async Task Rpm06_Failed_Discovery_Releases_Lease() => await RunAsync(RepoDbProviderContract.FailedDiscoveryReleasesLeaseAsync);

    [ExternalDatabaseFact("UOW_TEST_MYSQL")]
    public async Task Rpm07_Same_Root_Overlap_Is_Rejected() => await RunAsync(RepoDbProviderContract.SameRootOverlapIsRejectedAsync);

    [ExternalDatabaseFact("UOW_TEST_MYSQL")]
    public async Task Rpm08_Independent_Roots_Insert_Concurrently() => await RunAsync(RepoDbProviderContract.IndependentRootsCanInsertConcurrentlyAsync);

    private static async Task RunAsync(Func<IRepoDbProviderHarness, Task> contract)
    {
        await using var harness = new MySqlProviderHarness();
        await contract(harness);
    }
}
