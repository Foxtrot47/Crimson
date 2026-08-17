using Crimson.Core;
using Crimson.Models;

namespace Crimson.Infrastructure;

public sealed class FileLibraryService : ILibraryService, IDisposable
{
    private readonly string _appDataRoot;
    private readonly FileSystemWatcher _watcher;
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private long _sequence;

    public FileLibraryService(string appDataRoot)
    {
        _appDataRoot = Path.GetFullPath(
            string.IsNullOrWhiteSpace(appDataRoot)
                ? throw new ArgumentException("Application data root is required.", nameof(appDataRoot))
                : appDataRoot);
        Directory.CreateDirectory(_appDataRoot);
        _watcher = new FileSystemWatcher(_appDataRoot, "*.json")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };
        _watcher.Changed += OnStateChanged;
        _watcher.Created += OnStateChanged;
        _watcher.Deleted += OnStateChanged;
        _watcher.Renamed += OnStateChanged;
    }

    public event EventHandler<LibrarySnapshot>? Changed;

    public Task<LibrarySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var localStates = ReadLocalStates();
            var games = ReadGames(cancellationToken)
                .Where(game => !game.IsDlc())
                .Select(game => ToSummary(game, localStates))
                .OrderBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            var snapshot = new LibrarySnapshot(Interlocked.Increment(ref _sequence), games);
            return Task.FromResult(snapshot);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var snapshot = new LibrarySnapshot(
                Interlocked.Increment(ref _sequence),
                [],
                $"Library data could not be loaded: {exception.GetType().Name}");
            return Task.FromResult(snapshot);
        }
    }

    private async void OnStateChanged(object sender, FileSystemEventArgs e)
    {
        await _publishGate.WaitAsync();
        try
        {
            var snapshot = await GetSnapshotAsync();
            Changed?.Invoke(this, snapshot);
        }
        finally
        {
            _publishGate.Release();
        }
    }

    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnStateChanged;
        _watcher.Created -= OnStateChanged;
        _watcher.Deleted -= OnStateChanged;
        _watcher.Renamed -= OnStateChanged;
        _watcher.Dispose();
        _publishGate.Dispose();
    }

    private IEnumerable<Game> ReadGames(CancellationToken cancellationToken)
    {
        var metadataRoot = Path.Combine(_appDataRoot, "metadata");
        if (!Directory.Exists(metadataRoot))
            yield break;

        foreach (var path in Directory.EnumerateFiles(metadataRoot, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!AtomicJsonFile.TryRead<Game>(path, out var game) || game is null)
                continue;
            var expectedName = $"{StorageKeyCodec.Encode(game.AppName)}.json";
            if (!string.Equals(Path.GetFileName(path), expectedName, StringComparison.OrdinalIgnoreCase))
                continue;
            yield return game;
        }
    }

    private IReadOnlyDictionary<string, LocalAppState> ReadLocalStates()
    {
        var path = Path.Combine(_appDataRoot, "localstate.json");
        return AtomicJsonFile.TryRead<Dictionary<string, LocalAppState>>(path, out var states) && states is not null
            ? states
            : new Dictionary<string, LocalAppState>(StringComparer.Ordinal);
    }

    private static GameSummary ToSummary(
        Game game,
        IReadOnlyDictionary<string, LocalAppState> localStates)
    {
        var imageValue = game.Metadata.KeyImages
            .FirstOrDefault(image => image.Type == "DieselGameBoxTall")?.Url;
        Uri? imageUri = null;
        if (Uri.TryCreate(imageValue, UriKind.Absolute, out var parsedImage) &&
            parsedImage.Scheme is "https")
            imageUri = parsedImage;

        var isInstalled = localStates.TryGetValue(game.AppName, out var state) &&
            state.InstallStatus is InstallState.Installed or InstallState.NeedUpdate;
        return new GameSummary(
            game.AppName,
            game.AppTitle,
            imageUri,
            game.AssetInfos.Windows.BuildVersion,
            isInstalled);
    }
}

public static class AppDataPaths
{
    public static string GetDefaultRoot()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Crimson");

        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return !string.IsNullOrWhiteSpace(xdgDataHome)
            ? Path.Combine(xdgDataHome, "Crimson")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "share",
                "Crimson");
    }
}
