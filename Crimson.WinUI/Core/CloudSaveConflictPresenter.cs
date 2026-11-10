using Crimson.Resources;
using System;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Models;
using Crimson.Providers;
using Crimson.Utils;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;

namespace Crimson.Core;

/// <summary>
/// Presents the cloud save conflict dialog on behalf of <see cref="CloudSaveManager"/>,
/// which runs on background launch/exit paths. The dialog shows which copy is newer and
/// both file sizes and lets the user keep the local or the cloud copy, or skip syncing.
/// </summary>
internal sealed class CloudSaveConflictPresenter : IDisposable
{
    private readonly FrameworkElement _root;
    private readonly CloudSaveManager _cloudSaves;
    private readonly LauncherLibrary _library;
    private readonly DialogCoordinator _dialogs;
    private readonly ILogger _log;
    private readonly Func<bool> _isWindowVisible;
    private readonly Action _ensureWindowVisible;
    private readonly Func<CloudSaveConflict, Task<CloudSaveResolution>> _resolver;
    private TaskCompletionSource<CloudSaveResolution>? _pending;
    private volatile bool _disposed;

    public CloudSaveConflictPresenter(
        FrameworkElement root,
        CloudSaveManager cloudSaves,
        LauncherLibrary library,
        DialogCoordinator dialogs,
        ILogger log,
        Func<bool> isWindowVisible,
        Action ensureWindowVisible)
    {
        _root = root;
        _cloudSaves = cloudSaves;
        _library = library;
        _dialogs = dialogs;
        _log = log;
        _isWindowVisible = isWindowVisible;
        _ensureWindowVisible = ensureWindowVisible;
        _resolver = ResolveAsync;
        cloudSaves.ConflictResolver = _resolver;
    }

    private Task<CloudSaveResolution> ResolveAsync(CloudSaveConflict conflict)
    {
        var completion = new TaskCompletionSource<CloudSaveResolution>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _pending, completion);
        if (!_disposed && _root.DispatcherQueue.TryEnqueue(async () => await ShowDialogAsync(conflict, completion)))
            return completion.Task;

        // Disposed or the queue refused the dialog: resolve now so the sync releases its gate.
        Interlocked.CompareExchange(ref _pending, null, completion);
        completion.TrySetResult(CloudSaveResolution.Skip);
        return completion.Task;
    }

    private async Task ShowDialogAsync(
        CloudSaveConflict conflict,
        TaskCompletionSource<CloudSaveResolution> completion)
    {
        try
        {
            if (_disposed)
            {
                completion.TrySetResult(CloudSaveResolution.Skip);
                return;
            }

            // A hidden-to-tray window still reports IsLoaded with a live XamlRoot, so a
            // dialog shown there would wait forever on input the user can never give.
            if (!_isWindowVisible() || _root.XamlRoot is null)
                _ensureWindowVisible();
            if (_root.XamlRoot is null || !_isWindowVisible())
            {
                completion.TrySetResult(CloudSaveResolution.Skip);
                return;
            }

            var dialog = CreateDialog(conflict);
            var result = await _dialogs.ShowAsync(async () => await dialog.ShowAsync());
            completion.TrySetResult(result switch
            {
                ContentDialogResult.Primary => CloudSaveResolution.KeepCloud,
                ContentDialogResult.Secondary => CloudSaveResolution.KeepLocal,
                _ => CloudSaveResolution.Skip
            });
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Cloud save conflict dialog could not be shown");
            completion.TrySetResult(CloudSaveResolution.Skip);
        }
        finally
        {
            Interlocked.CompareExchange(ref _pending, null, completion);
        }
    }

    /// <summary>
    /// Detaches the resolver from the manager so a closed window is never called back, and
    /// resolves a pending conflict as "skip" so the sync does not wait on a dialog that
    /// can no longer be shown.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        if (ReferenceEquals(_cloudSaves.ConflictResolver, _resolver))
            _cloudSaves.ConflictResolver = null;
        Interlocked.Exchange(ref _pending, null)?.TrySetResult(CloudSaveResolution.Skip);
    }

    private ContentDialog CreateDialog(CloudSaveConflict conflict)
    {
        var localNewer = conflict.LocalTimestampUtc > conflict.RemoteTimestampUtc;
        var localLine = FormatCopy(
            UiStrings.CloudSaveLocalCopyFormat, conflict.LocalSizeBytes, conflict.LocalTimestampUtc, localNewer);
        var cloudLine = FormatCopy(
            UiStrings.CloudSaveCloudCopyFormat, conflict.RemoteSizeBytes, conflict.RemoteTimestampUtc, !localNewer);

        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = UiStrings.CloudSaveConflictMessage,
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock { Text = localLine, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = cloudLine, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock
        {
            Text = UiStrings.CloudSaveConflictPrompt,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });

        var title = UiStrings.CloudSaveConflictTitle;
        var gameTitle = _library.GetGameInfo(conflict.AppName)?.AppTitle;
        if (!string.IsNullOrWhiteSpace(gameTitle))
            title = UiStrings.Format(UiStrings.TitleDetailFormat, gameTitle, title);

        var dialog = new ContentDialog
        {
            XamlRoot = _root.XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = UiStrings.KeepCloudSaves,
            SecondaryButtonText = UiStrings.KeepLocalSaves,
            CloseButtonText = UiStrings.SkipCloudSync,
            DefaultButton = ContentDialogButton.None,
            MaxWidth = 480
        };
        dialog.Resources["ContentDialogMaxWidth"] = 480d;
        return dialog;
    }

    private static string FormatCopy(string format, long sizeBytes, DateTimeOffset timestampUtc, bool isNewer)
    {
        var size = sizeBytes >= 0 ? SizeFormatter.Format(sizeBytes) : UiStrings.CloudSaveSizeUnknown;
        var line = UiStrings.Format(format, size, timestampUtc.ToLocalTime().ToString("g"));
        return isNewer ? line + UiStrings.CloudSaveNewerSuffix : line;
    }
}
