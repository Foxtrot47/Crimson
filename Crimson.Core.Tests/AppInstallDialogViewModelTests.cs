using System.Text;
using Crimson.Core;
using Crimson.Models;
using Crimson.Repository;
using Crimson.Utils;
using Crimson.ViewModels;
using Serilog;

namespace Crimson.Tests;

public sealed class AppInstallDialogViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"crimson-install-dialog-{Guid.NewGuid():N}");
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

    [Fact]
    public async Task OlderInitializationCannotOverwriteNewerGame()
    {
        var repository = new ControlledManifestRepository();
        var storage = new Storage(_logger, _root, Path.Combine(_root, "games"));
        var firstGame = CreateGame("first", "First", "first-box");
        var secondGame = CreateGame("second", "Second", "second-box");
        storage.SaveMetaData(firstGame);
        storage.SaveMetaData(secondGame);
        var library = CreateLibrary(storage, repository);
        var installer = new InstallManager(
            _logger,
            library,
            repository,
            storage,
            new DownloadManager(_logger, new HttpClient()),
            new UnusedShortcutManager(),
            new AllowInstallPermissionChecker());
        var viewModel = new AppInstallDialogViewModel(
            _logger,
            installer,
            library,
            storage,
            new ImmediateUiDispatcher());

        var firstInitialization = viewModel.InitializeAsync(firstGame);
        var secondInitialization = viewModel.InitializeAsync(secondGame);
        repository.Complete("second", CreateJsonManifest(downloadBytes: 200, writeBytes: 20));
        await secondInitialization;
        repository.Complete("first", CreateJsonManifest(downloadBytes: 100, writeBytes: 10));
        await firstInitialization;

        Assert.Equal("Second", viewModel.GameTitle);
        Assert.Equal("second-box", viewModel.GameImageUrl);
        Assert.Equal("200 B", viewModel.TotalDownloadSize);
        Assert.Equal("20 B", viewModel.TotalInstallSize);
        Assert.False(viewModel.IsLoadingContent);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private LibraryManager CreateLibrary(Storage storage, IStoreRepository repository)
    {
        var auth = new AuthManager(
            _logger,
            storage,
            new TestCredentialProtector(),
            new HttpClient());
        return new LibraryManager(_logger, repository, storage, auth);
    }

    private static Game CreateGame(string appName, string title, string imageUrl) => new()
    {
        AppName = appName,
        AppTitle = title,
        AssetInfos = new AssetInfos
        {
            Windows = new Asset
            {
                AppName = appName,
                BuildVersion = "1.0",
                CatalogItemId = appName,
                Namespace = "test"
            }
        },
        Metadata = new Metadata
        {
            Id = appName,
            KeyImages = [new KeyImage { Type = "DieselGameBox", Url = imageUrl }]
        }
    };

    private static byte[] CreateJsonManifest(int downloadBytes, int writeBytes)
    {
        const string guid = "00000001000000020000000300000004";
        var json = $$"""
            {
              "FileManifestList":[{
                "Filename":"game.exe",
                "FileHash":"{{EncodeDecimalBytes(new byte[20])}}",
                "FileChunkParts":[{
                  "Guid":"{{guid}}",
                  "Offset":"{{EncodeDecimalBytes(BitConverter.GetBytes(0))}}",
                  "Size":"{{EncodeDecimalBytes(BitConverter.GetBytes(writeBytes))}}"
                }]
              }],
              "ChunkHashList":{"{{guid}}":"{{EncodeDecimalBytes(BitConverter.GetBytes(0L))}}"},
              "ChunkShaList":{"{{guid}}":"{{new string('0', 40)}}"},
              "DataGroupList":{"{{guid}}":"{{EncodeDecimalBytes([8])}}"},
              "ChunkFilesizeList":{"{{guid}}":"{{EncodeDecimalBytes(BitConverter.GetBytes((long)downloadBytes))}}"}
            }
            """;
        return Encoding.UTF8.GetBytes(json);
    }

    private static string EncodeDecimalBytes(IEnumerable<byte> bytes) =>
        string.Concat(bytes.Select(value => value.ToString("D3")));

    private sealed class ImmediateUiDispatcher : IUiDispatcher
    {
        public bool TryEnqueue(Action callback)
        {
            callback();
            return true;
        }
    }

    private sealed class AllowInstallPermissionChecker : IInstallPermissionChecker
    {
        public InstallPermissionCheckResult Check(string folderPath) => new(true);
    }

    private sealed class UnusedShortcutManager : IGameShortcutManager
    {
        public Task CreateAsync(Game game, GameShortcutLocation location) =>
            throw new NotSupportedException();

        public void Remove(Game game)
        {
        }
    }

    private sealed class ControlledManifestRepository : IStoreRepository
    {
        private readonly Dictionary<string, TaskCompletionSource<byte[]>> _manifests = new();

        public void Complete(string appName, byte[] manifest) =>
            GetSource(appName).SetResult(manifest);

        public Task<GetManifestUrlData> GetManifestUrls(
            string nameSpace,
            string catalogItem,
            string appName,
            string platform = "Windows",
            string label = "Live") => Task.FromResult(new GetManifestUrlData
            {
                BaseUrls = [],
                ManifestUrls = [appName],
                ManifestHash = string.Empty
            });

        public Task<byte[]> GetGameManifest(GetManifestUrlData urlData) =>
            GetSource(urlData.ManifestUrls[0]).Task;

        private TaskCompletionSource<byte[]> GetSource(string appName)
        {
            lock (_manifests)
            {
                if (!_manifests.TryGetValue(appName, out var source))
                {
                    source = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _manifests.Add(appName, source);
                }

                return source;
            }
        }

        public Task<Metadata> FetchGameMetaData(string nameSpace, string catalogItemId) =>
            throw new NotSupportedException();

        public Task<IEnumerable<Asset>> FetchGameAssets(string platform = "Windows", string label = "Live") =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<StoreSearchResult>> SearchStore(
            string query,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DownloadFileAsync(string url, string destinationPath) =>
            throw new NotSupportedException();

        public Task<string> GetGameToken() => throw new NotSupportedException();

        public Task<byte[]?> GetOwnershipToken(string nameSpace, string catalogItemId) =>
            throw new NotSupportedException();
    }
}
