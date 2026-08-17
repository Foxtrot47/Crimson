using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Crimson.Core;
using Crimson.Models;
using Crimson.Repository;
using Crimson.Utils;
using Serilog;
using Microsoft.Extensions.Logging.Abstractions;

namespace Crimson.Tests;

public sealed class SyntheticUpdateLifecycleTests
{
    private static readonly string FixtureRoot = Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "SyntheticGame");

    [Fact]
    public async Task InstallRestartAndUpdate_PublishesExactNewVersionAndPreservesUserFiles()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"crimson-lifecycle-{Guid.NewGuid():N}");
        var stateRoot = Path.Combine(sandbox, "state");
        var installRoot = Path.Combine(sandbox, "game");
        Directory.CreateDirectory(installRoot);
        using var logger = new LoggerConfiguration().CreateLogger();
        try
        {
            var versionOne = CreateHarness(logger, stateRoot, "old.manifest");
            versionOne.Storage.SaveMetaData(CreateGame("1.0.0"));

            var installResult = await RunOperationAsync(
                versionOne.Manager,
                new InstallItem("CrimsonSyntheticGame", ActionType.Install, installRoot));
            Assert.Equal(ActionStatus.Success, installResult.Status);
            await AssertInstalledFilesAsync("old", installRoot);

            var unchangedPath = Path.Combine(installRoot, "Data", "unchanged.txt");
            var unchangedWriteTime = File.GetLastWriteTimeUtc(unchangedPath);
            var userFile = Path.Combine(installRoot, "Data", "user-save.dat");
            await File.WriteAllTextAsync(userFile, "preserve me");

            SimulateInterruptedPublication(versionOne.Storage, installRoot);
            var versionTwo = CreateHarness(logger, stateRoot, "new.manifest");
            await AssertInstalledFilesAsync("old", installRoot);
            Assert.False(File.Exists(Path.Combine(installRoot, "Data", "added.txt")));
            Assert.False(File.Exists(UpdateTransactionState.GetJournalPath(installRoot)));
            var updatedGame = versionTwo.Library.GetGameInfo("CrimsonSyntheticGame");
            Assert.NotNull(updatedGame);
            updatedGame.AssetInfos.Windows.BuildVersion = "2.0.0";
            versionTwo.Storage.SaveMetaData(updatedGame);

            versionTwo.Manager.UpdatePublicationFaultInjector = relativePath =>
            {
                if (relativePath == "Data/added.txt")
                    throw new IOException("Injected publication failure.");
            };
            var failedUpdate = await RunOperationAsync(
                versionTwo.Manager,
                new InstallItem("CrimsonSyntheticGame", ActionType.Update, installRoot));
            Assert.Equal(ActionStatus.Failed, failedUpdate.Status);
            await AssertInstalledFilesAsync("old", installRoot);
            Assert.True(File.Exists(Path.Combine(installRoot, "Data", "removed.txt")));
            Assert.False(File.Exists(Path.Combine(installRoot, "Data", "added.txt")));
            Assert.Equal("preserve me", await File.ReadAllTextAsync(userFile));

            var retry = CreateHarness(logger, stateRoot, "new.manifest");
            var updateResult = await RunOperationAsync(
                retry.Manager,
                new InstallItem("CrimsonSyntheticGame", ActionType.Update, installRoot));
            Assert.Equal(ActionStatus.Success, updateResult.Status);
            await AssertInstalledFilesAsync("new", installRoot);
            Assert.False(File.Exists(Path.Combine(installRoot, "Data", "removed.txt")));
            Assert.True(File.Exists(Path.Combine(installRoot, "Data", "added.txt")));
            Assert.Equal("preserve me", await File.ReadAllTextAsync(userFile));
            Assert.Equal(unchangedWriteTime, File.GetLastWriteTimeUtc(unchangedPath));

            var persisted = new Storage(logger, stateRoot);
            var installation = persisted.LocalAppStateDictionary["CrimsonSyntheticGame"];
            Assert.Equal(InstallState.Installed, installation.InstallStatus);
            Assert.Equal("2.0.0", installation.Version);
            Assert.Equal("2.0.0", installation.CachedManifestVersion);
        }
        finally
        {
            if (Directory.Exists(sandbox))
                Directory.Delete(sandbox, recursive: true);
        }
    }

    private static Harness CreateHarness(
        ILogger logger,
        string stateRoot,
        string manifestName)
    {
        var storage = new Storage(logger, stateRoot);
        var repository = new SyntheticRepository(manifestName);
        var auth = new AuthManager(logger, storage, new HttpClient(new RejectingHandler()));
        var library = new LibraryManager(logger, repository, storage, auth);
        var contentClient = new HttpClient(new FixtureContentHandler());
        var downloads = new DownloadManager(NullLogger<DownloadManager>.Instance, contentClient);
        var manager = new InstallManager(logger, library, repository, storage, downloads);
        return new Harness(storage, library, manager);
    }

    private static Game CreateGame(string buildVersion) => new()
    {
        AppName = "CrimsonSyntheticGame",
        AppTitle = "Crimson Synthetic Game",
        AssetInfos = new AssetInfos
        {
            Windows = new Asset
            {
                AppName = "CrimsonSyntheticGame",
                BuildVersion = buildVersion,
                CatalogItemId = "synthetic-catalog",
                Namespace = "synthetic"
            }
        },
        Metadata = new Metadata
        {
            Id = "synthetic-catalog",
            Title = "Crimson Synthetic Game",
            Namespace = "synthetic"
        }
    };

    private static async Task<InstallItem> RunOperationAsync(InstallManager manager, InstallItem operation)
    {
        var completion = new TaskCompletionSource<InstallItem>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStatusChanged(InstallItem item)
        {
            if (item.AppName == operation.AppName && item.Status is
                ActionStatus.Success or ActionStatus.Failed or ActionStatus.Cancelled)
                completion.TrySetResult(item);
        }

        manager.InstallationStatusChanged += OnStatusChanged;
        try
        {
            manager.AddToQueue(operation);
            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await WaitUntilAsync(() => manager.CurrentInstall is null, TimeSpan.FromSeconds(5));
            return result;
        }
        finally
        {
            manager.InstallationStatusChanged -= OnStatusChanged;
        }
    }
    private static void SimulateInterruptedPublication(Storage storage, string installRoot)
    {
        var oldState = storage.LocalAppStateDictionary["CrimsonSyntheticGame"];
        var transaction = UpdateTransactionState.Create(
            installRoot,
            ["Data/changed.txt"],
            ["Data/added.txt"],
            ["Data/removed.txt"],
            [],
            JsonSerializer.Serialize(oldState));
        transaction.Phase = UpdateTransactionPhase.Published;
        Directory.CreateDirectory(transaction.StagingRoot);
        foreach (var relativePath in transaction.ChangedPaths.Concat(transaction.RemovedPaths))
        {
            var livePath = Path.Combine(installRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var backupPath = Path.Combine(
                transaction.BackupRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            File.Move(livePath, backupPath);
        }

        var changedPath = Path.Combine(installRoot, "Data", "changed.txt");
        File.WriteAllText(changedPath, "partially published");
        File.WriteAllText(Path.Combine(installRoot, "Data", "added.txt"), "partially added");
        var journalPath = UpdateTransactionState.GetJournalPath(installRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        File.WriteAllText(journalPath, JsonSerializer.Serialize(transaction));
    }


    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Install manager did not reach its terminal state.");
            await Task.Delay(10);
        }
    }

    private static async Task AssertInstalledFilesAsync(string version, string installRoot)
    {
        using var expected = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(FixtureRoot, "expected.json")));
        foreach (var property in expected.RootElement.GetProperty(version).GetProperty("files").EnumerateObject())
        {
            var path = Path.Combine(
                installRoot,
                property.Name.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"Missing installed file: {property.Name}");
            var bytes = await File.ReadAllBytesAsync(path);
            Assert.Equal(property.Value.GetProperty("size").GetInt64(), bytes.LongLength);
            Assert.Equal(
                property.Value.GetProperty("sha1").GetString(),
                Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant());
        }
    }

    private sealed record Harness(Storage Storage, LibraryManager Library, InstallManager Manager);

    private sealed class SyntheticRepository(string manifestName) : IStoreRepository
    {
        public Task<RepositoryResult<GetManifestUrlData>> GetManifestUrls(
            string nameSpace,
            string catalogItem,
            string appName,
            EpicPayloadPlatform platform,
            string label = "Live",
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(EpicPayloadPlatform.Windows, platform);
            var manifest = File.ReadAllBytes(Path.Combine(FixtureRoot, manifestName));
            return Task.FromResult(RepositoryResult<GetManifestUrlData>.Success(new GetManifestUrlData
            {
                BaseUrls = ["https://download.epicgames.com/synthetic"],
                ManifestUrls = ["https://download.epicgames.com/synthetic/" + manifestName],
                ManifestHash = Convert.ToHexString(SHA256.HashData(manifest))
            }));
        }

        public Task<RepositoryResult<byte[]>> GetGameManifest(
            GetManifestUrlData urlData,
            CancellationToken cancellationToken = default) => Task.FromResult(
            RepositoryResult<byte[]>.Success(
                File.ReadAllBytes(Path.Combine(FixtureRoot, manifestName))));

        public Task<RepositoryResult<Metadata>> FetchGameMetaData(
            string nameSpace,
            string catalogItemId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<RepositoryResult<IReadOnlyList<Asset>>> FetchGameAssets(
            EpicPayloadPlatform platform,
            string label = "Live",
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<RepositoryResult<long>> DownloadFileAsync(
            string url,
            string destinationPath,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<RepositoryResult<string>> GetGameToken(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FixtureContentHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            const string prefix = "/synthetic/";
            var path = request.RequestUri?.AbsolutePath
                ?? throw new InvalidOperationException("Synthetic request URI is missing.");
            if (!path.StartsWith(prefix, StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            var relative = Uri.UnescapeDataString(path[prefix.Length..])
                .Replace('/', Path.DirectorySeparatorChar);
            var file = Path.GetFullPath(Path.Combine(FixtureRoot, relative));
            if (!file.StartsWith(Path.GetFullPath(FixtureRoot), StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(file))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(File.ReadAllBytes(file))
            });
        }
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => throw new InvalidOperationException(
            "Authentication HTTP client is not used by the synthetic lifecycle.");
    }
}
