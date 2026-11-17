using Crimson.Resources;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Core;
using Crimson.Models;
using Crimson.Providers;
using Crimson.Providers.Epic;
using Crimson.ViewModels;
using Crimson.Utils;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Serilog;

namespace Crimson.Controls;

public sealed partial class GameManageDialog : UserControl
{
    private readonly IFolderLauncher _folderLauncher;
    private readonly IGameShortcutManager _shortcutManager;
    private bool _isOpen;
    private Uri? _requestedStoreUri;
    private CancellationTokenSource? _dialogCancellation;

    public GameDlcViewModel Dlcs { get; }
    public InstallLanguageSelection Languages { get; } = new();
    public GameInfoViewModel? ViewModel { get; private set; }
    public string GameTitle => ViewModel?.Game?.AppTitle ?? string.Empty;
    public string GameStatus => ViewModel?.GameStatusText ?? string.Empty;
    public string InstalledBuild => ViewModel?.InstalledVersion ?? string.Empty;
    public string InstallLocation => ViewModel?.Game?.LocalAppState?.InstallPath ?? string.Empty;
    public bool CanViewStore => ViewModel?.CanViewStore == true;

    public GameManageDialog()
    {
        Dlcs = new GameDlcViewModel(
            App.GetService<EpicLibraryManager>(),
            App.GetService<EpicInstallManager>(),
            App.GetService<IUiDispatcher>());
        InitializeComponent();
        _folderLauncher = App.GetService<IFolderLauncher>();
        _shortcutManager = App.GetService<IGameShortcutManager>();
    }

    public bool IsOpen => _isOpen;

    public async Task<Uri?> ShowAsync(
        GameInfoViewModel viewModel,
        XamlRoot xamlRoot,
        double availableWidth,
        double availableHeight)
    {
        if (_isOpen || viewModel.Game is null) return null;

        ViewModel = viewModel;
        using var cancellation = new CancellationTokenSource();
        _dialogCancellation = cancellation;
        var appName = viewModel.Game.AppName;
        // DLC management and the cached install size are Epic services; other stores show neither.
        var isEpic = GameId.Parse(appName).ProviderId == EpicLauncherProvider.ProviderId;
        ResetDialog();
        ConfigureActions(appName, isEpic);
        InstalledSizeText.Text = UiStrings.LoadingEllipsis;
        var installedGame = isEpic ? App.GetService<EpicLibraryManager>().GetGameInfo(GameId.Parse(appName).LocalId) : null;
        var loading = Task.WhenAll(
            installedGame is null ? Task.CompletedTask : Dlcs.LoadAsync(installedGame, cancellation.Token),
            isEpic ? LoadInstalledSizeAsync(appName, cancellation.Token) : Task.CompletedTask,
            LoadLanguagesAsync(appName, cancellation));
        Uri? storeUri = null;
        _isOpen = true;
        try
        {
            ManageContentDialog.XamlRoot = xamlRoot;
            UpdateSize(availableWidth, availableHeight);
            Bindings.Update();
            await App.GetService<DialogCoordinator>().ShowAsync(
                async () => await ManageContentDialog.ShowAsync(), cancellation.Token);
            storeUri = _requestedStoreUri;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            _isOpen = false;
            cancellation.Cancel();
            if (ReferenceEquals(_dialogCancellation, cancellation)) _dialogCancellation = null;
            Dlcs.Close();
            Languages.Clear();
            await loading;
        }
        return storeUri;
    }

    private async Task LoadInstalledSizeAsync(string appName, CancellationToken cancellationToken)
    {
        try
        {
            var size = await App.GetService<EpicInstallManager>().GetCachedInstallSizeBytes(GameId.Parse(appName).LocalId, cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;
            InstalledSizeText.Text = size.HasValue
                ? StorageSizeFormatter.FormatMebibytes(size.Value / 1048576d)
                : UiStrings.Unavailable;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void ConfigureActions(string appName, bool isEpic)
    {
        var installer = App.GetService<LauncherInstaller>();
        static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
        VerifyRepairButton.Visibility = Show(installer.Supports(appName, ActionType.Repair));
        MoveButton.Visibility = Show(installer.Supports(appName, ActionType.Move));
        UninstallSection.Visibility = Show(installer.Supports(appName, ActionType.Uninstall));
        DlcSectionItem.Visibility = Show(isEpic);
        InstalledSizeLabel.Visibility = Show(isEpic);
        InstalledSizeText.Visibility = Show(isEpic);
        LanguagesSectionItem.Visibility = Visibility.Collapsed;
    }

    private async Task LoadLanguagesAsync(string appName, CancellationTokenSource cancellation)
    {
        var installer = App.GetService<LauncherInstaller>();
        if (!installer.Supports(appName, ActionType.Repair)) return;
        try
        {
            var choice = await installer.GetInstallLanguages(appName, cancellation.Token);
            if (choice is null || !ReferenceEquals(cancellation, _dialogCancellation) || cancellation.IsCancellationRequested) return;
            Languages.Load(choice);
            LanguagesSectionItem.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Log.Warning("Install languages could not be loaded: {ErrorType}", exception.GetType().Name);
            if (!ReferenceEquals(cancellation, _dialogCancellation)) return;
            ShowActionError(UiStrings.InstallLanguagesCouldNotBeLoaded, UiStrings.ReopenPropertiesAndTryAgain);
            installer.ReportPreparationFailure(appName, ActionType.Repair, new OperationFailure(
                OperationFailureKind.Unknown, "install_languages_unavailable", UiStrings.InstallLanguagesCouldNotBeLoaded,
                UiStrings.ReopenPropertiesAndTryAgain, false));
        }
    }

    public void UpdateSize(double availableWidth, double availableHeight)
    {
        DialogContent.Width = Math.Clamp(availableWidth - 120, 520, 820);
        DialogContent.Height = Math.Clamp(availableHeight - 140, 440, 620);
    }

    public void Close()
    {
        _dialogCancellation?.Cancel();
        if (_isOpen)
            ManageContentDialog.Hide();
    }

    private void ResetDialog()
    {
        _requestedStoreUri = null;
        ClearActionError();
        SectionList.SelectedIndex = 0;
        GeneralPanel.Visibility = Visibility.Visible;
        InstalledFilesPanel.Visibility = Visibility.Collapsed;
        DlcPanel.Visibility = Visibility.Collapsed;
        LanguagesPanel.Visibility = Visibility.Collapsed;
        CancelDlcConfirmation();
        UninstallAction.Visibility = Visibility.Visible;
        UninstallConfirmation.Visibility = Visibility.Collapsed;
        ShortcutProgressRing.IsActive = false;
        ShortcutProgressRing.Visibility = Visibility.Collapsed;
        ShortcutIcon.Visibility = Visibility.Visible;
        CreateShortcutButton.IsEnabled = true;
    }

    private void ManageContentDialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args) =>
        SectionList.Focus(FocusState.Programmatic);

    private void SectionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SectionList.SelectedItem is not ListViewItem selectedItem) return;

        var section = selectedItem.Tag as string;
        GeneralPanel.Visibility = section == "General" ? Visibility.Visible : Visibility.Collapsed;
        InstalledFilesPanel.Visibility = section == "InstalledFiles" ? Visibility.Visible : Visibility.Collapsed;
        DlcPanel.Visibility = section == "Dlc" ? Visibility.Visible : Visibility.Collapsed;
        LanguagesPanel.Visibility = section == "Languages" ? Visibility.Visible : Visibility.Collapsed;
        CancelUninstallConfirmation();
        CancelDlcConfirmation();
    }

    private void CloseButtonClicked(object sender, RoutedEventArgs e) => Close();

    private void DialogContentKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape) return;

        if (DlcConfirmation.Visibility == Visibility.Visible)
            CancelDlcConfirmation();
        else if (UninstallConfirmation.Visibility == Visibility.Visible)
            CancelUninstallConfirmation();
        else
            Close();
        e.Handled = true;
    }

    private void BrowseFilesClicked(object sender, RoutedEventArgs e)
    {
        ClearActionError();
        if (!Directory.Exists(InstallLocation))
        {
            ShowBrowseError("installation_folder_missing", UiStrings.TheInstallationFolderCouldNotBeFound);
            return;
        }

        try
        {
            _folderLauncher.Open(InstallLocation);
        }
        catch (Exception exception)
        {
            Log.Warning(
                "Failed to open installation folder for {AppName}: {ErrorType}",
                ViewModel?.Game?.AppName,
                exception.GetType().Name);
            ShowBrowseError("installation_folder_open_failed", UiStrings.TheInstallationFolderCouldNotBeOpened);
        }
    }

    private async void CreateShortcutClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.Game is not { } game || _dialogCancellation is not { } cancellation) return;

        ClearActionError();
        CreateShortcutButton.IsEnabled = false;
        ShortcutIcon.Visibility = Visibility.Collapsed;
        ShortcutProgressRing.Visibility = Visibility.Visible;
        ShortcutProgressRing.IsActive = true;
        try
        {
            await _shortcutManager.CreateAsync(game, GameShortcutLocation.Desktop, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Log.Warning(
                "Failed to create desktop shortcut for {AppName}: {ErrorType}",
                game.AppName,
                exception.GetType().Name);
            if (!ReferenceEquals(cancellation, _dialogCancellation)) return;
            ShowActionError(UiStrings.TheDesktopShortcutCouldNotBeCreated, UiStrings.TryCreatingItAgainFromGameProperties);
            App.GetService<LauncherInstaller>().ReportPreparationFailure(game.AppName, Crimson.Models.ActionType.Install,
                new Crimson.Models.OperationFailure(Crimson.Models.OperationFailureKind.Storage,
                    "desktop_shortcut_failed", UiStrings.TheDesktopShortcutCouldNotBeCreated,
                    UiStrings.TryCreatingItAgainFromGameProperties, false));
        }
        finally
        {
            if (ReferenceEquals(cancellation, _dialogCancellation))
            {
                CreateShortcutButton.IsEnabled = true;
                ShortcutProgressRing.IsActive = false;
                ShortcutProgressRing.Visibility = Visibility.Collapsed;
                ShortcutIcon.Visibility = Visibility.Visible;
            }
        }
    }

    private void ApplyLanguagesClicked(object sender, RoutedEventArgs e)
    {
        if (!Languages.CanApply) return;
        var languages = Languages.SelectedCodes;
        Close();
        ViewModel?.ChangeLanguagesCommand.Execute(languages);
    }

    private void VerifyRepairClicked(object sender, RoutedEventArgs e)
    {
        Close();
        ViewModel?.VerifyRepairCommand.Execute(null);
    }

    private async void MoveGameClicked(object sender, RoutedEventArgs e)
    {
        Close();
        if (ViewModel is not null)
            await ViewModel.MoveGameCommand.ExecuteAsync(null);
    }

    private void RequestUninstallClicked(object sender, RoutedEventArgs e)
    {
        UninstallAction.Visibility = Visibility.Collapsed;
        UninstallConfirmation.Visibility = Visibility.Visible;
        CancelUninstallButton.Focus(FocusState.Programmatic);
    }

    private void CancelUninstallClicked(object sender, RoutedEventArgs e) =>
        CancelUninstallConfirmation();

    private void CancelUninstallConfirmation()
    {
        UninstallConfirmation.Visibility = Visibility.Collapsed;
        UninstallAction.Visibility = Visibility.Visible;
    }

    private void ConfirmUninstallClicked(object sender, RoutedEventArgs e)
    {
        Close();
        ViewModel?.UninstallCommand.Execute(null);
    }

    private void ViewInStoreClicked(object sender, RoutedEventArgs e)
    {
        _requestedStoreUri = ViewModel?.StorePageUri;
        if (_requestedStoreUri is not null) Close();
    }

    private void ApplyDlcClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var plan = Dlcs.GetChangePlan();
            if (plan.Count == 0) return;
            if (plan.Any(item => item.Action == Crimson.Models.ActionType.Uninstall))
            {
                DlcList.IsEnabled = false;
                DlcSearch.IsEnabled = false;
                Dlcs.IsConfirmingRemoval = true;
                DlcConfirmation.Visibility = Visibility.Visible;
                CancelDlcButton.Focus(FocusState.Programmatic);
                return;
            }
            QueueDlcChanges();
        }
        catch (Exception exception)
        {
            ShowDlcError(exception);
        }
    }

    private void ConfirmDlcClicked(object sender, RoutedEventArgs e) => QueueDlcChanges();

    private void QueueDlcChanges()
    {
        ClearActionError();
        try
        {
            Dlcs.ApplyChanges();
            Close();
        }
        catch (Exception exception)
        {
            ShowDlcError(exception);
        }
    }

    private void ShowDlcError(Exception exception)
    {
        Log.Warning("DLC changes could not be queued: {ErrorType}", exception.GetType().Name);
        ShowActionError(UiStrings.DLCChangesCouldNotBeQueued, UiStrings.ReopenPropertiesAndTryAgain);
        App.GetService<LauncherInstaller>().ReportPreparationFailure(ViewModel?.Game?.AppName ?? string.Empty,
            Crimson.Models.ActionType.Install, new Crimson.Models.OperationFailure(
                Crimson.Models.OperationFailureKind.Unknown, "dlc_changes_failed",
                UiStrings.DLCChangesCouldNotBeQueued, UiStrings.ReopenPropertiesAndTryAgain, false));
    }

    private void CancelDlcClicked(object sender, RoutedEventArgs e) => CancelDlcConfirmation();

    private void CancelDlcConfirmation()
    {
        DlcList.IsEnabled = true;
        DlcSearch.IsEnabled = true;
        Dlcs.IsConfirmingRemoval = false;
        DlcConfirmation.Visibility = Visibility.Collapsed;
    }

    private void ClearActionError()
    {
        ActionErrorText.Text = string.Empty;
        ActionErrorText.Visibility = Visibility.Collapsed;
    }

    private void ShowActionError(string message, string suggestion)
    {
        ActionErrorText.Text = UiStrings.Format(UiStrings.MessageSuggestionFormat, message, suggestion);
        ActionErrorText.Visibility = Visibility.Visible;
    }

    private void ShowBrowseError(string code, string message)
    {
        ShowActionError(message, UiStrings.CheckThatTheInstallationFolderIsAvailableAndTryAgain);
        App.GetService<LauncherInstaller>().ReportPreparationFailure(ViewModel?.Game?.AppName ?? string.Empty,
            Crimson.Models.ActionType.Install, new Crimson.Models.OperationFailure(
                Crimson.Models.OperationFailureKind.Storage, code, message,
                UiStrings.CheckThatTheInstallationFolderIsAvailableAndTryAgain, false));
    }
}
