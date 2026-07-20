using Xunit;

namespace Ariadne.Core.Tests;

/// <summary>
/// Proves the solution builds and the test pipeline is green from the first commit.
/// Replaced by real feature tests as functionality lands.
/// </summary>
public class SmokeTests
{
    [Fact]
    public void Core_assembly_is_referenced_and_builds()
    {
        Assert.Equal("0.0.0-dev", AriadneCore.Version);
    }
}
