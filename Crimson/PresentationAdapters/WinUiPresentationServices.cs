using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Core;
using Crimson.Models;
using Crimson.Presentation;
using Crimson.Utils;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using H.NotifyIcon;
using Windows.Storage.Pickers;

namespace Crimson.PresentationAdapters;

public sealed class WinUiUiDispatcher : IUiDispatcher
{
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread()
        ?? throw new InvalidOperationException("A UI dispatcher is unavailable.");

    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        if (_dispatcher.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(() =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    action();
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }))
        {
            completion.TrySetException(new InvalidOperationException("The UI dispatcher rejected the operation."));
        }
        return completion.Task;
    }
}

public sealed class WinUiFolderPickerService : IFolderPickerService
{
    public async Task<string?> PickFolderAsync(
        string? suggestedPath,
        CancellationToken cancellationToken = default)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder
        };
        picker.FileTypeFilter.Add("*");
        var window = ((App)Application.Current).GetWindow();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var folder = await picker.PickSingleFolderAsync().AsTask(cancellationToken);
        return folder?.Path;
    }
}

public sealed class WinUiExternalPathLauncher(IPlatformPathLauncher launcher) : IExternalPathLauncher
{
    public Task OpenAsync(string path, CancellationToken cancellationToken = default) =>
        launcher.OpenDirectoryAsync(path, cancellationToken);
}

public sealed class WinUiInstallDialogService : IInstallDialogService
{
    private Func<string, CancellationToken, Task>? _show;
    private Action? _close;

    public void Register(Func<string, CancellationToken, Task> show, Action close)
    {
        _show = show ?? throw new ArgumentNullException(nameof(show));
        _close = close ?? throw new ArgumentNullException(nameof(close));
    }

    public void Unregister()
    {
        _show = null;
        _close = null;
    }

    public Task ShowAsync(string appName, CancellationToken cancellationToken = default) =>
        _show?.Invoke(appName, cancellationToken)
        ?? Task.FromException(new InvalidOperationException("The install dialog host is unavailable."));

    public void Close() => _close?.Invoke();
}

public sealed class WinUiEpicAuthenticationService : IEpicAuthenticationService, IDisposable
{
    private readonly AuthManager _authentication;
    private bool _disposed;

    public WinUiEpicAuthenticationService(AuthManager authentication)
    {
        _authentication = authentication;
        Snapshot = Map(authentication.AuthenticationStatus);
        _authentication.AuthStatusChanged += OnStatusChanged;
    }

    public EpicAuthenticationSnapshot Snapshot { get; private set; }

    public event EventHandler<EpicAuthenticationSnapshot>? Changed;

    public async Task<EpicAuthenticationSnapshot> CheckAsync(CancellationToken cancellationToken = default)
    {
        var status = await _authentication.CheckAuthStatus(cancellationToken);
        return await ApplyAsync(status);
    }

    public async Task<EpicAuthenticationSnapshot> LoginWithExchangeCodeAsync(
        string exchangeCode,
        CancellationToken cancellationToken = default)
    {
        await _authentication.DoExchangeLogin(exchangeCode, cancellationToken);
        return await ApplyAsync(_authentication.AuthenticationStatus);
    }

    public Task<string?> GetAccessToken(CancellationToken cancellationToken = default) =>
        _authentication.GetAccessToken(cancellationToken);

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _authentication.Logout();
        Apply(Map(AuthenticationStatus.LoggedOut));
    }

    private void OnStatusChanged(object sender, AuthStatusChangedEventArgs args) =>
        Apply(Map(args.NewStatus));

    private async Task<EpicAuthenticationSnapshot> ApplyAsync(AuthenticationStatus status)
    {
        string? displayName = null;
        if (status == AuthenticationStatus.LoggedIn)
            displayName = (await _authentication.GetUserData())?.DisplayName;
        var snapshot = Map(status) with { DisplayName = displayName };
        Apply(snapshot);
        return snapshot;
    }

    private void Apply(EpicAuthenticationSnapshot snapshot)
    {
        Snapshot = snapshot;
        Changed?.Invoke(this, snapshot);
    }

    private static EpicAuthenticationSnapshot Map(AuthenticationStatus status) => status switch
    {
        AuthenticationStatus.Checking => new(EpicAuthenticationState.Checking),
        AuthenticationStatus.LoggedIn => new(EpicAuthenticationState.LoggedIn),
        AuthenticationStatus.LoginFailed => new(EpicAuthenticationState.Failed, Error: "Login failed"),
        _ => new(EpicAuthenticationState.LoggedOut)
    };

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _authentication.AuthStatusChanged -= OnStatusChanged;
    }
}

public sealed class WinUiGameWorkflowService : IGameWorkflowService, IDisposable
{
    private readonly LibraryManager _library;
    private readonly InstallManager _install;
    private readonly Storage _storage;
    private bool _disposed;

    public WinUiGameWorkflowService(
        LibraryManager library,
        InstallManager install,
        Storage storage)
    {
        _library = library;
        _install = install;
        _storage = storage;
        _library.GameStatusUpdated += OnGameChanged;
        _install.InstallationStatusChanged += OnOperationChanged;
        _install.InstallProgressUpdate += OnOperationChanged;
    }

    public event EventHandler<GamePresentationData>? GameChanged;

    public event EventHandler<InstallOperationSnapshot?>? OperationChanged;

    public InstallOperationSnapshot? CurrentOperation => Map(_install.CurrentInstall);

    public GamePresentationData? GetGame(string appName) => Map(_library.GetGameInfo(appName));

    public IReadOnlyList<GamePresentationData> GetDlcs(string appName) =>
        _library.GetDlcsForGame(appName).Select(Map).OfType<GamePresentationData>().ToArray();

    public IReadOnlyList<GamePresentationData> GetQueuedGames() =>
        _install.GetQueueItemNames().Select(GetGame).OfType<GamePresentationData>().ToArray();

    public IReadOnlyList<GamePresentationData> GetHistoryGames() =>
        _install.GetHistoryItemsNames().Select(GetGame).OfType<GamePresentationData>().ToArray();

    public Task LaunchAsync(string appName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _library.LaunchApp(appName);
    }

    public Task<InstallCommandResult> EnqueueAsync(
        InstallOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        var item = new InstallItem(request.AppName, request.Action, request.Location)
        {
            MoveLocation = request.MoveLocation
        };
        return _install.EnqueueAsync(item, cancellationToken);
    }

    public Task<InstallCommandResult> CancelAsync(
        string appName,
        CancellationToken cancellationToken = default) =>
        _install.CancelAsync(appName, cancellationToken);

    public Task<InstallCommandResult> PauseAsync(CancellationToken cancellationToken = default) =>
        _install.PauseAsync(cancellationToken);

    public Task<InstallCommandResult> ResumeAsync(CancellationToken cancellationToken = default) =>
        _install.ResumeAsync(cancellationToken);

    public async Task<InstallContentSize> GetContentSizeAsync(
        string appName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var size = await _install.GetGameDownloadInstallSizes(appName);
        return new InstallContentSize(size.totalDownloadSizeMb, size.totalWriteSizeMb);
    }

    public async Task<DriveSpaceSnapshot?> GetDriveSpaceAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var drive = await _storage.GetDriveInfo(path);
        return new DriveSpaceSnapshot(drive.AvailableFreeSpace, drive.TotalSize);
    }

    private void OnGameChanged(Game game)
    {
        if (Map(game) is { } snapshot)
            GameChanged?.Invoke(this, snapshot);
    }

    private void OnOperationChanged(InstallItem item) =>
        OperationChanged?.Invoke(this, Map(item));

    private InstallOperationSnapshot? Map(InstallItem? item)
    {
        if (item is null || GetGame(item.AppName) is not { } game)
            return null;
        return new InstallOperationSnapshot(
            item.AppName,
            game.Title,
            game.ImageUri,
            item.Action,
            item.Status,
            item.ProgressPercentage,
            item.WrittenSizeMiB,
            item.TotalWriteSizeMb,
            item.DownloadSpeedRawMiB,
            item.StatusMessage);
    }

    private static GamePresentationData? Map(Game? game)
    {
        if (game is null)
            return null;
        var image = game.Metadata.KeyImages?
            .FirstOrDefault(value => value.Type is "DieselGameBox" or "DieselGameBoxTall")?.Url;
        Uri? imageUri = null;
        if (Uri.TryCreate(image, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps)
            imageUri = parsed;
        return new GamePresentationData(
            game.AppName,
            game.AppTitle,
            imageUri,
            game.AssetInfos.Windows.BuildVersion,
            game.LocalAppState?.InstallStatus ?? InstallState.NotInstalled,
            game.LocalAppState?.InstallPath,
            game.IsDlc());
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _library.GameStatusUpdated -= OnGameChanged;
        _install.InstallationStatusChanged -= OnOperationChanged;
        _install.InstallProgressUpdate -= OnOperationChanged;
    }
}

public sealed class WinUiDesktopApplicationControl : IDesktopApplicationControl
{
    public void ToggleMainWindow()
    {
        var window = ((App)Application.Current).GetWindow();
        if (window is null)
            return;
        if (window.Visible)
            window.Hide();
        else
            window.Show();
    }

    public void Quit()
    {
        App.HandleClosedEvents = false;
        ((App)Application.Current).GetWindow()?.Close();
    }
}
