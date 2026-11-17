using Crimson.Resources;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Crimson.Models;
using Crimson.Providers;

namespace Crimson.ViewModels;

public sealed class GameImportChoice
{
    public required string AppName { get; init; }
    public required string Title { get; init; }
    public string? ImageUrl { get; init; }

    public string? DetectedPath { get; init; }

    public string LocationHint => DetectedPath ?? UiStrings.ChooseTheGameFolderToImport;
}

public partial class GameImportViewModel(LauncherInstaller installer, LauncherLibrary library, LauncherRegistry registry) : ObservableObject
{
    private int _version;
    private int _accountGeneration = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItems))]
    private IReadOnlyList<GameImportChoice> _items = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImport), nameof(CanChooseLocation))]
    private GameImportChoice? _selectedItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    private string? _location;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImport), nameof(CanEdit), nameof(CanChooseLocation))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string _statusMessage = string.Empty;

    public string ProviderName { get; private set; } = string.Empty;
    public bool HasItems => Items.Count > 0;
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);
    public bool CanEdit => !IsBusy;
    public bool CanChooseLocation => !IsBusy && SelectedItem is not null;
    public bool CanImport => !IsBusy && SelectedItem is not null && !string.IsNullOrWhiteSpace(Location);

    /// <summary>Supplied by the view; returns the picked folder or null when the user cancels.</summary>
    public event Func<Task<string?>>? FolderPickerRequested;

    partial void OnSelectedItemChanged(GameImportChoice? value) => Location = value?.DetectedPath;

    public async Task LoadAsync(string providerId, CancellationToken cancellationToken)
    {
        Close();
        var version = _version;
        _accountGeneration = library.AccountGeneration;
        ProviderName = registry.Get(providerId).DisplayName;
        OnPropertyChanged(nameof(ProviderName));
        IsBusy = true;
        StatusMessage = UiStrings.LookingForInstalledGames;
        try
        {
            if (!installer.SupportsImport(providerId))
            {
                StatusMessage = UiStrings.NoGamesAvailableToImport;
                return;
            }
            var games = await library.GetLibraryData().WaitAsync(cancellationToken);
            if (!IsCurrent(version, cancellationToken)) return;
            var candidates = await installer.DiscoverImportCandidates(providerId, cancellationToken);
            if (!IsCurrent(version, cancellationToken)) return;
            Items = BuildChoices(games, candidates, providerId);
            SelectedItem = Items.FirstOrDefault(choice => choice.DetectedPath is not null);
            StatusMessage = HasItems ? string.Empty : UiStrings.NoGamesAvailableToImport;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!IsCurrent(version, cancellationToken)) return;
            StatusMessage = UiStrings.Format(UiStrings.MessageCloseAndRetryFormat, UiStrings.InstalledGamesCouldNotBeDetected);
            installer.ReportPreparationFailure(string.Empty, ActionType.Import, new OperationFailure(
                OperationFailureKind.Unknown, "import_detection_failed", UiStrings.InstalledGamesCouldNotBeDetected,
                UiStrings.CloseTheImportWindowAndTryAgain, false));
        }
        finally
        {
            if (version == _version) IsBusy = false;
        }
    }

    private List<GameImportChoice> BuildChoices(
        IEnumerable<LauncherGame> games, IReadOnlyList<ImportCandidate> candidates, string providerId)
    {
        var detected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var candidate in candidates) detected.TryAdd(candidate.AppName, candidate.InstallPath);
        return games
            .Where(game => IsImportable(game, providerId))
            .Select(game => new GameImportChoice
            {
                AppName = game.AppName,
                Title = game.AppTitle,
                ImageUrl = game.CoverImageUrl,
                DetectedPath = detected.GetValueOrDefault(game.AppName)
            })
            .OrderBy(choice => choice.DetectedPath is null)
            .ThenBy(choice => choice.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private bool IsImportable(LauncherGame game, string providerId) =>
        game.Id.ProviderId == providerId && !game.IsDlc() && game.CanInstall &&
        game.LocalAppState is null or { InstallStatus: InstallState.NotInstalled } &&
        installer.Supports(game.AppName, ActionType.Import);

    public async Task PickLocationAsync()
    {
        if (!CanChooseLocation || FolderPickerRequested is null) return;
        var version = _version;
        var choice = SelectedItem;
        var folder = await FolderPickerRequested.Invoke();
        if (string.IsNullOrWhiteSpace(folder) || version != _version || !ReferenceEquals(choice, SelectedItem)) return;
        Location = folder;
    }

    /// <summary>Validates the folder and queues the import; false keeps the dialog open.</summary>
    public async Task<bool> ImportSelectedAsync(CancellationToken cancellationToken)
    {
        if (!CanImport) return false;
        var version = _version;
        var choice = SelectedItem!;
        var location = Location!;
        IsBusy = true;
        try
        {
            var failure = await installer.ValidateImportLocation(choice.AppName, location, cancellationToken);
            if (!IsCurrent(version, cancellationToken)) return false;
            if (failure is not null)
            {
                StatusMessage = UiStrings.Format(UiStrings.MessageSuggestionFormat, failure.Message, failure.SuggestedAction);
                return false;
            }
            if (installer.TryAddToQueue(new InstallItem(choice.AppName, ActionType.Import, location))) return true;
            StatusMessage = UiStrings.Format(UiStrings.MessageCloseAndRetryFormat, UiStrings.ThisImportCouldNotBeQueued);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return false; }
        catch (Exception exception)
        {
            if (!IsCurrent(version, cancellationToken)) return false;
            var failure = Crimson.Core.OperationFailureClassifier.Classify(exception);
            StatusMessage = UiStrings.Format(UiStrings.MessageSuggestionFormat, failure.Message, failure.SuggestedAction);
            installer.ReportPreparationFailure(choice.AppName, ActionType.Import, exception);
            return false;
        }
        finally
        {
            if (version == _version) IsBusy = false;
        }
    }

    private bool IsCurrent(int version, CancellationToken cancellationToken) =>
        version == _version && !cancellationToken.IsCancellationRequested && _accountGeneration == library.AccountGeneration;

    public void Close()
    {
        _version++;
        _accountGeneration = -1;
        SelectedItem = null;
        Items = [];
        Location = null;
        StatusMessage = string.Empty;
        IsBusy = false;
    }
}
