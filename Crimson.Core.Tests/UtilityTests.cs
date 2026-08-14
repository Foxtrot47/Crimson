using Crimson.Utils;

namespace Crimson.Tests;

public sealed class UtilityTests
{
    [Fact]
    public void RollingHash_IsDeterministicAndSensitiveToOrder()
    {
        var first = RollingHash.ComputeHash(new byte[] { 1, 2, 3, 4 });
        var repeated = RollingHash.ComputeHash(new byte[] { 1, 2, 3, 4 });
        var reordered = RollingHash.ComputeHash(new byte[] { 4, 3, 2, 1 });

        Assert.Equal(first, repeated);
        Assert.NotEqual(first, reordered);
    }
}
