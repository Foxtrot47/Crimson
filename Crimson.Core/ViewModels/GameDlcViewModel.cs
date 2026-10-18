using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Crimson.Core;
using Crimson.Models;
using Crimson.Utils;

namespace Crimson.ViewModels;

public partial class GameDlcItem : ObservableObject
{
    public required string AppName { get; init; }
    public required string Title { get; init; }
    public string? ImageUrl { get; init; }
    public bool IsInstalled { get; init; }
    public string InstallLabel => $"Install {Title}";

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _canEdit;

    [ObservableProperty]
    private string _sizeText = string.Empty;
}

public partial class GameDlcViewModel : ObservableObject
{
    private readonly LibraryManager _library;
    private readonly InstallManager _installer;
    private readonly IUiDispatcher _dispatcher;
    private readonly List<GameDlcItem> _allItems = [];
    private string? _baseAppName;
    private int _generation;

    [ObservableProperty]
    private IReadOnlyList<GameDlcItem> _items = [];

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _emptyMessage = "No DLC available.";

    [ObservableProperty]
    private bool _isEmpty = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply), nameof(ShowApply))]
    private bool _hasChanges;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply), nameof(IsBlocked))]
    private bool _canEdit;

    [ObservableProperty]
    private string _blockedMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowApply))]
    private bool _isConfirmingRemoval;

    public bool CanApply => CanEdit && HasChanges;
    public bool ShowApply => HasChanges && !IsConfirmingRemoval;
    public bool IsBlocked => !CanEdit;

    public GameDlcViewModel(LibraryManager library, InstallManager installer, IUiDispatcher dispatcher)
    {
        _library = library;
        _installer = installer;
        _dispatcher = dispatcher;
    }

    public async Task LoadAsync(Game game, CancellationToken cancellationToken)
    {
        Close();
        _baseAppName = game.AppName;
        var generation = _generation;
        SearchText = string.Empty;
        foreach (var dlc in _library.GetOwnedDlcsForGame(game.AppName).OrderBy(item => item.AppTitle, StringComparer.CurrentCultureIgnoreCase))
        {
            var installed = IsInstalled(dlc.LocalAppState?.InstallStatus);
            var item = new GameDlcItem
            {
                AppName = dlc.AppName,
                Title = dlc.AppTitle,
                IsInstalled = installed,
                IsSelected = installed,
                ImageUrl = GetImageUrl(dlc)
            };
            item.PropertyChanged += ItemChanged;
            _allItems.Add(item);
        }
        ApplyFilter();
        HasChanges = false;
        IsConfirmingRemoval = false;
        RefreshAvailability();
        _installer.OperationUpdated += OperationUpdated;
        _library.GameStatusUpdated += GameStatusUpdated;

        try
        {
            foreach (var item in _allItems.ToList())
            {
                var bytes = await _installer.GetCachedInstallSizeBytes(item.AppName, cancellationToken);
                if (generation != _generation || cancellationToken.IsCancellationRequested) return;
                _dispatcher.TryEnqueue(() =>
                {
                    if (generation != _generation || cancellationToken.IsCancellationRequested) return;
                    item.SizeText = bytes is >= 0 ? StorageSizeFormatter.FormatMebibytes(bytes.Value / 1048576d) : string.Empty;
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public IReadOnlyList<InstallItem> GetChangePlan()
    {
        RefreshAvailability();
        if (!CanEdit) throw new InvalidOperationException(BlockedMessage);
        var game = _library.GetGameInfo(_baseAppName!);
        return CreateChangePlan(game, _allItems, _library.GetOwnedDlcsForGame(game.AppName));
    }

    public void ApplyChanges()
    {
        var plan = GetChangePlan();
        foreach (var item in plan)
            _installer.AddToQueue(item);
    }

    internal static IReadOnlyList<InstallItem> CreateChangePlan(
        Game game, IEnumerable<GameDlcItem> choices, IEnumerable<Game> availableDlcs)
    {
        var basePath = GetBaseInstallPath(game);
        var available = availableDlcs.ToDictionary(item => item.AppName, StringComparer.Ordinal);
        var plan = new List<InstallItem>();
        foreach (var choice in choices.Where(item => item.IsSelected != item.IsInstalled))
        {
            if (!available.TryGetValue(choice.AppName, out var dlc))
                throw new InvalidOperationException("The DLC library changed. Reopen Properties and try again.");
            if (choice.IsSelected == IsInstalled(dlc.LocalAppState?.InstallStatus)) continue;
            var action = choice.IsSelected ? ActionType.Install : ActionType.Uninstall;
            var path = choice.IsSelected ? basePath : dlc.LocalAppState?.InstallPath;
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("The DLC installation location is unavailable.");
            plan.Add(new InstallItem(choice.AppName, action, path));
        }
        return plan;
    }

    private static string GetBaseInstallPath(Game game)
    {
        if (game.LocalAppState?.InstallStatus is not (InstallState.Installed or InstallState.NeedUpdate) ||
            string.IsNullOrWhiteSpace(game.LocalAppState.InstallPath))
            throw new InvalidOperationException("Install or repair the game before changing DLC.");
        return game.LocalAppState.InstallPath;
    }

    private void RefreshAvailability()
    {
        var game = _baseAppName is null ? null : _library.GetGameInfo(_baseAppName);
        var installed = game?.LocalAppState?.InstallStatus is InstallState.Installed or InstallState.NeedUpdate;
        var busy = _baseAppName is not null && _installer.GetActiveOperationForGame(_baseAppName) is not null;
        CanEdit = installed && !busy;
        BlockedMessage = CanEdit ? string.Empty : installed
            ? "Finish the current operation before changing DLC."
            : "Install or repair the game before changing DLC.";
        foreach (var item in _allItems) item.CanEdit = CanEdit;
    }

    private void ItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GameDlcItem.IsSelected))
            HasChanges = _allItems.Any(item => item.IsSelected != item.IsInstalled);
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        Items = _allItems.Where(item => item.Title.Contains(SearchText.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToList();
        IsEmpty = Items.Count == 0;
        EmptyMessage = _allItems.Count == 0 ? "No DLC available." : "No matching DLC.";
    }

    private void OperationUpdated(OperationSnapshot operation) => RefreshForGame(operation.AppName);
    private void GameStatusUpdated(Game game) => RefreshForGame(game.AppName);

    private void RefreshForGame(string appName)
    {
        if (_baseAppName is null || !_library.IsOperationForGame(appName, _baseAppName)) return;
        var generation = _generation;
        _dispatcher.TryEnqueue(() =>
        {
            if (generation == _generation) RefreshAvailability();
        });
    }

    private static bool IsInstalled(InstallState? state) =>
        state is not null and not InstallState.NotInstalled;

    private static string? GetImageUrl(Game game) => game.Metadata?.KeyImages?
        .Where(image => image.Type is "DieselGameBox" or "OfferImageWide" or "DieselGameBoxTall")
        .Select(image => image.Url)
        .FirstOrDefault(url => EpicEndpointPolicy.TryGetContentUri(url, out _));

    public void Close()
    {
        _generation++;
        _installer.OperationUpdated -= OperationUpdated;
        _library.GameStatusUpdated -= GameStatusUpdated;
        foreach (var item in _allItems) item.PropertyChanged -= ItemChanged;
        _allItems.Clear();
        _baseAppName = null;
    }
}
