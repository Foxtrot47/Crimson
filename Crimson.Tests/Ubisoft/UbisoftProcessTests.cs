using Crimson.Providers.Ubisoft;

namespace Crimson.Tests.Ubisoft;

public class UbisoftProcessTests
{
    [Fact]
    public async Task StartsNativeChildWithInheritedEventAndObservesExit()
    {
        var command = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        using var process = new UbisoftProcess();
        process.Start(new(1, "Process fixture", Path.GetDirectoryName(command)!, command, "/c exit 0"));
        process.SignalReady();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await process.WaitForExit(timeout.Token);
    }
}
