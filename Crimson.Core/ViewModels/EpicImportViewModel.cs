using Crimson.Resources;
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

public partial class EpicImportChoice : ObservableObject
{
    public required EpicInstallation Installation { get; init; }
    public required string Title { get; init; }
    public string? ImageUrl { get; init; }

    [ObservableProperty]
    private bool _isSelected;
}

public partial class EpicImportViewModel(
    EpicInstallDiscovery discovery, InstallManager installer, LibraryManager library, Storage storage) : ObservableObject
{
    private int _version;
    private int _accountGeneration = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImport), nameof(HasItems))]
    private IReadOnlyList<EpicImportChoice> _items = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImport), nameof(CanEdit))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string _statusMessage = UiStrings.DetectingEpicGamesLauncherInstallations;

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);
    public bool CanEdit => !IsBusy;
    public bool HasItems => Items.Count > 0;
    public bool CanImport => !IsBusy && Items.Any(item => item.IsSelected);

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        Close();
        var version = _version;
        _accountGeneration = storage.AccountGeneration;
        IsBusy = true;
        StatusMessage = UiStrings.DetectingEpicGamesLauncherInstallations;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await library.GetLibraryData().WaitAsync(cancellationToken);
            if (!IsCurrent(version, cancellationToken)) return;
            var scan = await discovery.DiscoverAsync(cancellationToken);
            lock (storage.AccountDataLock)
            {
                if (!IsCurrent(version, cancellationToken)) return;
                SetChoices(scan.Installations);
                StatusMessage = HasItems ? string.Empty : UiStrings.NoEligibleEpicGamesLauncherInstallationsFound;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!IsCurrent(version, cancellationToken)) return;
            ReportFailure("epic_install_detection_failed", UiStrings.EpicInstallationsCouldNotBeDetected);
        }
        finally
        {
            if (version == _version) IsBusy = false;
        }
    }

    public async Task<bool> ImportSelectedAsync(CancellationToken cancellationToken)
    {
        if (!CanImport) return false;
        var version = _version;
        var selected = Items.Where(item => item.IsSelected).Select(item => item.Installation).ToList();
        IsBusy = true;
        try
        {
            if (!await RevalidateSelectionAsync(selected, version, cancellationToken)) return false;
            var accepted = 0;
            foreach (var installation in selected)
                if (await installer.TryQueueEpicImportAsync(installation, _accountGeneration, cancellationToken)) accepted++;
            if (accepted > 0) return true;
            SetChoices(Items.Select(item => item.Installation).ToList());
            StatusMessage = UiStrings.TheseInstallationsAreNoLongerAvailableToImportCloseAndTryAgain;
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return false; }
        catch (Exception)
        {
            if (IsCurrent(version, cancellationToken))
                ReportFailure("epic_install_import_failed", UiStrings.TheSelectedEpicInstallationsCouldNotBeQueued);
            return false;
        }
        finally
        {
            if (version == _version) IsBusy = false;
        }
    }

    private async Task<bool> RevalidateSelectionAsync(
        IReadOnlyList<EpicInstallation> selected, int version, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var installation in selected)
        {
            await discovery.RevalidateAsync(installation, cancellationToken);
            if (!IsCurrent(version, cancellationToken)) return false;
        }
        return IsCurrent(version, cancellationToken);
    }

    private bool IsCurrent(int version, CancellationToken cancellationToken) =>
        version == _version && !cancellationToken.IsCancellationRequested && _accountGeneration == storage.AccountGeneration;

    private void SetChoices(IEnumerable<EpicInstallation> installations)
    {
        DetachChoices();
        Items = installations.Where(installer.CanImportEpicInstallation)
            .Select(CreateChoice)
            .OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        foreach (var item in Items) item.PropertyChanged += ChoiceChanged;
    }

    private EpicImportChoice CreateChoice(EpicInstallation installation)
    {
        var game = library.GetGameInfo(installation.AppName);
        return new EpicImportChoice
        {
            Installation = installation,
            Title = game.AppTitle,
            ImageUrl = game.Metadata?.KeyImages?
                .Where(image => image.Type == "DieselGameBoxTall")
                .Select(image => image.Url)
                .FirstOrDefault(url => EpicEndpointPolicy.TryGetContentUri(url, out _))
        };
    }

    private void ChoiceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(EpicImportChoice.IsSelected)) OnPropertyChanged(nameof(CanImport));
    }

    private void ReportFailure(string code, string message)
    {
        StatusMessage = UiStrings.Format(UiStrings.MessageCloseAndRetryFormat, message);
        installer.ReportPreparationFailure(string.Empty, ActionType.Import, new OperationFailure(
            OperationFailureKind.Unknown, code, message, UiStrings.CloseTheImportWindowAndTryAgain, false));
    }

    private void DetachChoices()
    {
        foreach (var item in Items) item.PropertyChanged -= ChoiceChanged;
    }

    public void Close()
    {
        _version++;
        _accountGeneration = -1;
        DetachChoices();
        Items = [];
        IsBusy = false;
    }
}
