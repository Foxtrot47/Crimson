using Crimson.Core;
using Crimson.Platform.Windows;

namespace Crimson.Tests;

public sealed class WindowsPlatformAdapterTests
{
    [Fact]
    public void ApplicationDirectories_UseWindowsUserLocations()
    {
        var directories = new WindowsApplicationDirectories();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.Equal(Path.Combine(localAppData, "Crimson"), directories.DataRoot);
        Assert.Equal(Path.Combine(directories.DataRoot, "logs"), directories.LogsDirectory);
        Assert.Equal(Path.Combine(directories.DataRoot, "webview2"), directories.WebViewDataDirectory);
        Assert.Equal(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            directories.DefaultInstallRoot);
    }

    [Fact]
    public void CredentialProtector_RoundTripsWithoutPlaintext()
    {
        const string value = "credential-canary-48b1";
        var protector = new WindowsCredentialProtector();

        var protectedValue = protector.Protect(value);

        Assert.DoesNotContain(value, protectedValue, StringComparison.Ordinal);
        Assert.Equal(value, protector.Unprotect(protectedValue));
    }

    [Fact]
    public async Task GameProcessRunner_WaitsForSuccessfulProcessExit()
    {
        var commandInterpreter = Environment.GetEnvironmentVariable("COMSPEC")
            ?? throw new InvalidOperationException("COMSPEC is unavailable.");
        var runner = new WindowsGameProcessRunner();

        await runner.RunAsync(new GameProcessStartInfo(
            commandInterpreter,
            "/d /c exit 0",
            Path.GetTempPath()));
    }
}
