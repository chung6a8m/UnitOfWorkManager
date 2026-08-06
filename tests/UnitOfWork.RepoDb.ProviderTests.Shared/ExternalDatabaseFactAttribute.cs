using Xunit;

namespace UnitOfWork.RepoDb.ProviderTests.Shared;

public sealed class ExternalDatabaseFactAttribute : FactAttribute
{
    public ExternalDatabaseFactAttribute(string environmentVariable)
    {
        if (string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(environmentVariable)))
        {
            Skip = $"Set {environmentVariable} to run this provider integration test.";
        }
    }
}
