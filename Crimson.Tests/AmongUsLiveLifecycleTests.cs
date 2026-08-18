using System.Diagnostics;
using System.Security.Cryptography;
using Crimson.Core;
using Crimson.Infrastructure;
using Crimson.Platform.Windows;
using Crimson.Models;
using Crimson.Repository;
using Crimson.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Xunit;

namespace Crimson.Tests;

public sealed class AmongUsLiveLifecycleTests
{
    private const string AppName = "963137e4c29d4c79a81323b8fab03a40";

    [LiveAmongUsFact]
    public async Task InstallRestartLaunchRepairAndUninstall()
    {
        var installRoot = Path.GetFullPath(LiveAmongUsFactAttribute.InstallRoot!);
        Assert.False(Directory.Exists(installRoot),
            $"Live lifecycle requires a nonexistent install root: {installRoot}");

        using var logger = new LoggerConfiguration().CreateLogger();
        using var harness = CreateHarness(logger);
        Assert.Equal(AuthenticationStatus.LoggedIn, await harness.Authentication.CheckAuthStatus());
        var games = await harness.Library.GetLibraryData(forceUpdate: true);
        var game = Assert.Single(games, candidate => candidate.AppName == AppName);

        var installStatuses = new List<ActionStatus>();
        var install = await RunOperationAsync(
            harness.Installation,
            new InstallItem(AppName, ActionType.Install, installRoot),
            installStatuses,
            TimeSpan.FromMinutes(30));
        Assert.Equal(ActionStatus.Success, install.Status);
        Assert.Equal(ActionStatus.Processing, installStatuses[0]);
        Assert.Equal(ActionStatus.Success, installStatuses[^1]);

        using var restarted = CreateHarness(logger);
        Assert.Equal(AuthenticationStatus.LoggedIn, await restarted.Authentication.CheckAuthStatus());
        await restarted.Installation.LoadPendingInstalls();
        Assert.Null(restarted.Installation.CurrentInstall);
        var restartedGame = restarted.Library.GetGameInfo(AppName);
        Assert.NotNull(restartedGame);
        Assert.Equal(InstallState.Installed, restartedGame.LocalAppState?.InstallStatus);
        Assert.Equal(installRoot, restartedGame.LocalAppState?.InstallPath);

        await ValidateLaunchAsync(restarted.Library, restartedGame);

        var manifestBytes = await restarted.Storage.GetCachedManifestBytes(
            AppName,
            restartedGame.AssetInfos.Windows.BuildVersion);
        Assert.NotNull(manifestBytes);
        var manifest = Manifest.ReadAll(manifestBytes);
        var repairCandidate = manifest.FileManifestList.Elements
            .Where(file => !file.Executable && file.FileSize > 0)
            .OrderBy(file => file.FileSize)
            .First();
        var repairPath = ManifestPath.ResolveUnderRoot(installRoot, repairCandidate.Path);
        await File.WriteAllTextAsync(repairPath, "intentional live repair corruption");

        var repairStatuses = new List<ActionStatus>();
        var repair = await RunOperationAsync(
            restarted.Installation,
            new InstallItem(AppName, ActionType.Repair, installRoot),
            repairStatuses,
            TimeSpan.FromMinutes(20));
        Assert.Equal(ActionStatus.Success, repair.Status);
        Assert.Equal(ActionStatus.Processing, repairStatuses[0]);
        Assert.Equal(ActionStatus.Success, repairStatuses[^1]);
        Assert.Equal(repairCandidate.ShaHash, SHA1.HashData(await File.ReadAllBytesAsync(repairPath)));

        var uninstallStatuses = new List<ActionStatus>();
        var uninstall = await RunOperationAsync(
            restarted.Installation,
            new InstallItem(AppName, ActionType.Uninstall, installRoot),
            uninstallStatuses,
            TimeSpan.FromMinutes(10));
        Assert.Equal(ActionStatus.Success, uninstall.Status);
        Assert.Equal(ActionStatus.Processing, uninstallStatuses[0]);
        Assert.Equal(ActionStatus.Success, uninstallStatuses[^1]);
        foreach (var file in manifest.FileManifestList.Elements)
            Assert.False(File.Exists(ManifestPath.ResolveUnderRoot(installRoot, file.Path)));
        Assert.Equal(
            InstallState.NotInstalled,
            restarted.Storage.LocalAppStateDictionary[AppName].InstallStatus);
    }

    private static LiveHarness CreateHarness(ILogger logger)
    {
        var directories = new WindowsApplicationDirectories();
        var storage = new Storage(logger, directories.DataRoot);
        var oauthClient = CreateClient(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(5));
        var apiClient = CreateClient(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10));
        var repositoryContentClient = CreateClient(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(15));
        var downloadContentClient = CreateClient(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(15));
        var authentication = new AuthManager(
            logger,
            storage,
            new WindowsCredentialProtector(),
            oauthClient);
        var repository = new EpicGamesRepository(
            authentication,
            NullLogger<EpicGamesRepository>.Instance,
            apiClient,
            repositoryContentClient);
        var libraryService = new LibraryService(repository, storage);
        var library = new LibraryManager(
            logger,
            repository,
            storage,
            authentication,
            new WindowsGameProcessRunner(),
            libraryService,
            new EpicLaunchPlanner(),
            new WindowsRuntimeProfileResolver(),
            new FileInstallRecoveryStatus());
        var downloads = new DownloadManager(
            NullLogger<DownloadManager>.Instance,
            downloadContentClient);
        var installation = new InstallManager(
            logger,
            library,
            repository,
            storage,
            downloads,
            new InstallFileSystemProbe());
        return new LiveHarness(
            storage,
            authentication,
            library,
            installation,
            oauthClient,
            apiClient,
            repositoryContentClient,
            downloadContentClient);
    }

    private static HttpClient CreateClient(TimeSpan timeout, TimeSpan connectTimeout)
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            MaxConnectionsPerServer = 16,
            ConnectTimeout = connectTimeout
        })
        {
            Timeout = timeout
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(EpicLauncherWebLogin.ApiUserAgent);
        return client;
    }

    private static async Task<InstallItem> RunOperationAsync(
        InstallManager manager,
        InstallItem operation,
        ICollection<ActionStatus> statuses,
        TimeSpan timeout)
    {
        var completion = new TaskCompletionSource<InstallItem>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(InstallItem item)
        {
            if (item.AppName != operation.AppName)
                return;
            statuses.Add(item.Status);
            if (item.Status is ActionStatus.Success or ActionStatus.Failed or ActionStatus.Cancelled)
                completion.TrySetResult(item);
        }

        manager.InstallationStatusChanged += OnChanged;
        try
        {
            manager.AddToQueue(operation);
            var result = await completion.Task.WaitAsync(timeout);
            await WaitUntilAsync(() => manager.CurrentInstall is null, TimeSpan.FromSeconds(10));
            return result;
        }
        finally
        {
            manager.InstallationStatusChanged -= OnChanged;
        }
    }

    private static async Task ValidateLaunchAsync(LibraryManager library, Game game)
    {
        var executable = game.LocalAppState?.Executable
            ?? throw new InvalidOperationException("Among Us has no launch executable after install.");
        var processName = Path.GetFileNameWithoutExtension(executable);
        var existingIds = Process.GetProcessesByName(processName).Select(process => process.Id).ToHashSet();
        Assert.Empty(existingIds);

        var launch = library.LaunchApp(AppName);
        var process = await WaitForProcessAsync(processName, existingIds, TimeSpan.FromSeconds(30));
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.CloseMainWindow();
                if (!process.WaitForExit(5000))
                    process.Kill(entireProcessTree: true);
            }
            process.Dispose();
        }
        await launch.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static async Task<Process> WaitForProcessAsync(
        string processName,
        IReadOnlySet<int> existingIds,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var process = Process.GetProcessesByName(processName)
                .FirstOrDefault(candidate => !existingIds.Contains(candidate.Id));
            if (process is not null)
                return process;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Launched process '{processName}' did not appear.");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Install manager did not reach its terminal state.");
            await Task.Delay(50);
        }
    }

    private sealed record LiveHarness(
        Storage Storage,
        AuthManager Authentication,
        LibraryManager Library,
        InstallManager Installation,
        HttpClient OAuthClient,
        HttpClient ApiClient,
        HttpClient RepositoryContentClient,
        HttpClient DownloadContentClient) : IDisposable
    {
        public void Dispose()
        {
            OAuthClient.Dispose();
            ApiClient.Dispose();
            RepositoryContentClient.Dispose();
            DownloadContentClient.Dispose();
        }
    }
}

public sealed class LiveAmongUsFactAttribute : FactAttribute
{
    public static string? InstallRoot { get; } =
        Environment.GetEnvironmentVariable("CRIMSON_AMONG_US_INSTALL_ROOT");

    public LiveAmongUsFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CRIMSON_RUN_AMONG_US_LIFECYCLE") != "1" ||
            string.IsNullOrWhiteSpace(InstallRoot))
        {
            Skip = "Set CRIMSON_RUN_AMONG_US_LIFECYCLE=1 and CRIMSON_AMONG_US_INSTALL_ROOT to run the destructive live lifecycle.";
        }
    }
}
