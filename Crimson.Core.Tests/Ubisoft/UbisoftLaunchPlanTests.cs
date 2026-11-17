using Crimson.Providers.Ubisoft;
using static Crimson.Core.Tests.Ubisoft.UbisoftLibraryTests;

namespace Crimson.Core.Tests.Ubisoft;

public sealed class UbisoftLaunchPlanTests : IDisposable
{
    private const string RegisterKey = @"HKEY_LOCAL_MACHINE\SOFTWARE\Ubisoft\Game\InstallDir";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crimson-ubisoft-launch-" + Guid.NewGuid().ToString("N"));

    public UbisoftLaunchPlanTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string File(string relative)
    {
        var path = Path.Combine(_root, relative.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, "fixture");
        return path;
    }

    private UbisoftLaunchPlan Resolve(string root, Func<string, string?>? registry = null) => UbisoftLaunchPlan.Resolve(
        new UbisoftInstalledGame { ProductId = 1, InstallPath = _root, Languages = ["de-DE"] },
        Product(1, config: "unused"), UbisoftProductConfig.Parse(root), registry ?? (_ => null));

    private const string OnlineAndOffline = """
        root:
          start_game:
            online:
              executables:
              - path:
                  relative: '\bin\Online.exe'
                working_directory:
                  relative: bin
                arguments: -online
            offline:
              executables:
              - path:
                  relative: '\bin\Offline.exe'
                arguments: -offline
          app_id: 35c5c607-2717-47d9-9323-7df47c6e1c4d
        """;

    [Fact]
    public void TheOnlineExecutableWinsWithItsWorkingDirectoryAndArguments()
    {
        var online = File(@"bin\Online.exe");
        File(@"bin\Offline.exe");
        var plan = Resolve(OnlineAndOffline);
        Assert.Equal(online, plan.Executable);
        Assert.Equal(Path.Combine(_root, "bin"), plan.WorkingDirectory);
        Assert.Equal("-online", plan.Arguments);
        Assert.Equal("35c5c607-2717-47d9-9323-7df47c6e1c4d", plan.ApplicationId);
        Assert.Equal("de-DE", plan.Language);
    }

    [Fact]
    public void AMissingOnlineExecutableFallsBackToTheOfflineOneAndTheInstallFolder()
    {
        var offline = File(@"bin\Offline.exe");
        var plan = Resolve(OnlineAndOffline);
        Assert.Equal(offline, plan.Executable);
        Assert.Equal(_root, plan.WorkingDirectory);
        Assert.Equal("-offline", plan.Arguments);
    }

    private const string Registered = """
        root:
          start_game:
            online:
              executables:
              - path:
                  register: HKEY_LOCAL_MACHINE\SOFTWARE\Ubisoft\Game\InstallDir
                  append: bin\Game.exe
                working_directory:
                  register: HKEY_LOCAL_MACHINE\SOFTWARE\Ubisoft\Game\InstallDir
                  append: bin
        """;

    [Fact]
    public void ARegisterPathIsReadFromTheKeysSystemSetupWrites()
    {
        var executable = File(@"bin\Game.exe");
        string? asked = null;
        var plan = Resolve(Registered, key => { asked = key; return _root + Path.DirectorySeparatorChar; });
        Assert.Equal(RegisterKey, asked);
        Assert.Equal(executable, plan.Executable);
        Assert.Equal(Path.Combine(_root, "bin"), plan.WorkingDirectory);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(@"C:\Elsewhere")]
    public void AMissingOrForeignRegisterValueFallsBackToTheInstallFolder(string? registered)
    {
        var executable = File(@"bin\Game.exe");
        var plan = Resolve(Registered, _ => registered);
        Assert.Equal(executable, plan.Executable);
    }

    [Fact]
    public void PathsEscapingTheInstallationAreRefused()
    {
        var outside = Path.Combine(Path.GetDirectoryName(_root)!, Path.GetFileName(_root) + "-outside.exe");
        System.IO.File.WriteAllText(outside, "fixture");
        try
        {
            var config = $"""
                root:
                  start_game:
                    online:
                      executables:
                      - path:
                          relative: '..\{Path.GetFileName(outside)}'
                """;
            Assert.Throws<InvalidOperationException>(() => Resolve(config));
        }
        finally { System.IO.File.Delete(outside); }
    }
}

public sealed class UbisoftRuntimeProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crimson-ubisoft-probe-" + Guid.NewGuid().ToString("N"));

    public UbisoftRuntimeProbeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private void Touch(string relative)
    {
        var path = Path.Combine(_root, relative.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "fixture");
    }

    [Theory]
    [InlineData(@"uplay_r1_loader64.dll")]
    [InlineData(@"bin\uplay_r2_loader.dll")]
    [InlineData(@"bin\x64\upc_r1_loader64.dll")]
    [InlineData(@"Binaries\Win64\UPC_R2_LOADER64.DLL")]
    public void AGameShippingALoaderUsesTheModernRuntime(string loader)
    {
        Touch(@"bin\Game.exe");
        Touch(loader);
        Assert.True(UbisoftRuntimeProbe.UsesModernRuntime(_root));
    }

    [Fact]
    public void AGameWithoutALoaderDoesNot()
    {
        Touch(@"bin\Game.exe");
        Touch(@"bin\uplay_r1.dll");
        Touch(@"bin\uplay_r1_loader.txt");
        Assert.False(UbisoftRuntimeProbe.UsesModernRuntime(_root));
        Assert.False(UbisoftRuntimeProbe.UsesModernRuntime(Path.Combine(_root, "missing")));
    }

    [Fact]
    public void TheSearchStopsAtItsDepthBound()
    {
        var deep = string.Join(Path.DirectorySeparatorChar, Enumerable.Range(0, UbisoftRuntimeProbe.MaximumDepth + 1).Select(i => "d" + i));
        Touch(Path.Combine(deep, "uplay_r1_loader64.dll"));
        Assert.False(UbisoftRuntimeProbe.UsesModernRuntime(_root));
    }
}
