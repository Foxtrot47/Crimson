using Crimson.Infrastructure;
using Xunit;

namespace Crimson.Infrastructure.Tests;

public sealed class FileInstallRecoveryStatusTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"crimson-recovery-status-{Guid.NewGuid():N}");

    [Fact]
    public void DetectsUpdateTransactionJournal()
    {
        var status = new FileInstallRecoveryStatus();
        Assert.False(status.HasUnresolvedTransaction(_root));
        var journal = Path.Combine(_root, ".Crimson", "update-transaction.json");
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        File.WriteAllText(journal, "{}");

        Assert.True(status.HasUnresolvedTransaction(_root));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
