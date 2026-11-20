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

    /// <summary>Where the store reports the game installed; the importer never asks for a folder.</summary>
    public required string DetectedPath { get; init; }
}

/// <summary>Offers the games a store itself reports as installed, with the folder it reports for each.</summary>
public partial class GameImportViewModel(LauncherInstaller installer, LauncherLibrary library) : ObservableObject
{
    private int _version;
    private int _accountGeneration = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItems))]
    private IReadOnlyList<GameImportChoice> _items = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    private GameImportChoice? _selectedItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImport), nameof(CanEdit))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string _statusMessage = string.Empty;

    public bool HasItems => Items.Count > 0;
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);
    public bool CanEdit => !IsBusy;
    public bool CanImport => !IsBusy && SelectedItem is not null;

    public async Task LoadAsync(string providerId, CancellationToken cancellationToken)
    {
        Close();
        var version = _version;
        _accountGeneration = library.AccountGeneration;
        IsBusy = true;
        StatusMessage = UiStrings.LookingForInstalledGames;
        try { await DetectInstallsAsync(providerId, version, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception) { ReportDetectionFailure(version, cancellationToken); }
        finally
        {
            if (version == _version) IsBusy = false;
        }
    }

    private async Task DetectInstallsAsync(string providerId, int version, CancellationToken cancellationToken)
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
        Items = BuildChoices(games, candidates);
        SelectedItem = Items.FirstOrDefault();
        StatusMessage = HasItems ? string.Empty : UiStrings.NoGamesAvailableToImport;
    }

    private void ReportDetectionFailure(int version, CancellationToken cancellationToken)
    {
        if (!IsCurrent(version, cancellationToken)) return;
        StatusMessage = UiStrings.Format(UiStrings.MessageCloseAndRetryFormat, UiStrings.InstalledGamesCouldNotBeDetected);
        installer.ReportPreparationFailure(string.Empty, ActionType.Import, new OperationFailure(
            OperationFailureKind.Unknown, "import_detection_failed", UiStrings.InstalledGamesCouldNotBeDetected,
            UiStrings.CloseTheImportWindowAndTryAgain, false));
    }

    private static List<GameImportChoice> BuildChoices(
        IEnumerable<LauncherGame> games, IReadOnlyList<ImportCandidate> candidates)
    {
        var known = new Dictionary<string, LauncherGame>(StringComparer.Ordinal);
        foreach (var game in games) known.TryAdd(game.AppName, game);
        return candidates
            .Where(candidate => IsImportable(known.GetValueOrDefault(candidate.AppName)))
            .Select(candidate =>
            {
                var game = known.GetValueOrDefault(candidate.AppName);
                return new GameImportChoice
                {
                    AppName = candidate.AppName,
                    Title = game?.AppTitle ?? candidate.Title,
                    ImageUrl = game?.CoverImageUrl,
                    DetectedPath = candidate.InstallPath
                };
            })
            .OrderBy(choice => choice.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    // A store may report an install the library does not know yet; only a game the library rules out is dropped.
    private static bool IsImportable(LauncherGame? game) =>
        game is null || (!game.IsDlc() && game.CanInstall &&
            game.LocalAppState is null or { InstallStatus: InstallState.NotInstalled });

    /// <summary>Validates the detected folder and queues the import; false keeps the dialog open.</summary>
    public async Task<bool> ImportSelectedAsync(CancellationToken cancellationToken)
    {
        if (!CanImport) return false;
        var version = _version;
        var choice = SelectedItem!;
        IsBusy = true;
        try { return await ValidateAndQueueAsync(choice, version, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return false; }
        catch (Exception exception) { return ReportImportFailure(choice.AppName, version, cancellationToken, exception); }
        finally
        {
            if (version == _version) IsBusy = false;
        }
    }

    private async Task<bool> ValidateAndQueueAsync(GameImportChoice choice, int version, CancellationToken cancellationToken)
    {
        var failure = await installer.ValidateImportLocation(choice.AppName, choice.DetectedPath, cancellationToken);
        if (!IsCurrent(version, cancellationToken)) return false;
        if (failure is not null)
        {
            StatusMessage = UiStrings.Format(UiStrings.MessageSuggestionFormat, failure.Message, failure.SuggestedAction);
            return false;
        }
        if (installer.TryAddToQueue(new InstallItem(choice.AppName, ActionType.Import, choice.DetectedPath))) return true;
        StatusMessage = UiStrings.Format(UiStrings.MessageCloseAndRetryFormat, UiStrings.ThisImportCouldNotBeQueued);
        return false;
    }

    private bool ReportImportFailure(string appName, int version, CancellationToken cancellationToken, Exception exception)
    {
        if (!IsCurrent(version, cancellationToken)) return false;
        var failure = Crimson.Core.OperationFailureClassifier.Classify(exception);
        StatusMessage = UiStrings.Format(UiStrings.MessageSuggestionFormat, failure.Message, failure.SuggestedAction);
        installer.ReportPreparationFailure(appName, ActionType.Import, exception);
        return false;
    }

    private bool IsCurrent(int version, CancellationToken cancellationToken) =>
        version == _version && !cancellationToken.IsCancellationRequested && _accountGeneration == library.AccountGeneration;

    public void Close()
    {
        _version++;
        _accountGeneration = -1;
        SelectedItem = null;
        Items = [];
        StatusMessage = string.Empty;
        IsBusy = false;
    }
}
