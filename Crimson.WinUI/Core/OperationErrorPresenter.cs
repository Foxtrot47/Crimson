using Crimson.Resources;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;
using Windows.System;

namespace Crimson.Core;

internal sealed class OperationErrorPresenter : IDisposable
{
    private readonly FrameworkElement _root;
    private readonly InstallManager _installer;
    private readonly LibraryManager _library;
    private readonly DialogCoordinator _dialogs;
    private readonly ILogger _log;
    private readonly Queue<OperationSnapshot> _pending = new();
    private readonly CancellationTokenSource _lifetime = new();
    private ContentDialog? _activeDialog;
    private bool _isShowing;
    private bool _closed;

    public OperationErrorPresenter(FrameworkElement root, InstallManager installer,
        LibraryManager library, DialogCoordinator dialogs, ILogger log)
    {
        _root = root;
        _installer = installer;
        _library = library;
        _dialogs = dialogs;
        _log = log;
        _installer.OperationError += OperationError;
        _root.Loaded += RootLoaded;
    }

    private void OperationError(OperationSnapshot operation)
    {
        if (!operation.ShowErrorDialog) return;
        _root.DispatcherQueue.TryEnqueue(async () =>
        {
            if (_closed) return;
            _pending.Enqueue(operation);
            await ShowPendingAsync();
        });
    }

    private async void RootLoaded(object sender, RoutedEventArgs args) => await ShowPendingAsync();

    private async Task ShowPendingAsync()
    {
        if (_closed || _isShowing || !_root.IsLoaded || _root.XamlRoot is null) return;
        _isShowing = true;
        try
        {
            while (!_closed && _pending.Count > 0)
            {
                var operation = _pending.Peek();
                var dialog = CreateDialog(operation);
                _activeDialog = dialog;
                await _dialogs.ShowAsync(async () => await dialog.ShowAsync(), _lifetime.Token);
                if (_closed) return;
                _pending.Dequeue();
            }
        }
        catch (OperationCanceledException) when (_closed)
        {
        }
        catch (Exception exception)
        {
            // Keep the notification queued if the UI cannot present it.
            _log.Error("Operation error dialog could not be shown: {ErrorType}", exception.GetType().Name);
        }
        finally
        {
            _activeDialog = null;
            _isShowing = false;
        }
    }

    private ContentDialog CreateDialog(OperationSnapshot operation)
    {
        var failure = operation.Failure ?? OperationFailureClassifier.Classify(null);
        var title = UiStrings.Format(UiStrings.OperationErrorFormat, OperationPresenter.GetActionLabel(operation.Action));
        var gameTitle = _library.GetGameInfo(operation.AppName)?.AppTitle;
        if (!string.IsNullOrWhiteSpace(gameTitle)) title = UiStrings.Format(UiStrings.TitleDetailFormat, gameTitle, title);
        var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock { Text = failure.Message, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = failure.SuggestedAction, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock
        {
            Text = UiStrings.Format(UiStrings.ErrorCodeFormat, failure.Code), FontSize = 12, Opacity = 0.75,
            IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(feedback);
        var dialog = new ContentDialog
        {
            XamlRoot = _root.XamlRoot, Title = title, Content = content,
            PrimaryButtonText = UiStrings.ReportProblem, CloseButtonText = UiStrings.Close,
            DefaultButton = ContentDialogButton.Close, MaxWidth = 480
        };
        dialog.Resources["ContentDialogMaxWidth"] = 480d;
        dialog.Resources["ContentDialogMinHeight"] = 0d;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            dialog.IsPrimaryButtonEnabled = false;
            await ReportAsync(operation, feedback);
            dialog.IsPrimaryButtonEnabled = true;
        };
        return dialog;
    }

    private async Task ReportAsync(OperationSnapshot operation, TextBlock feedback)
    {
        feedback.Visibility = Visibility.Collapsed;
        if (await OperationDiagnosticActions.TryReportAsync(operation,
            async uri => await Launcher.LaunchUriAsync(uri), _log)) return;
        feedback.Text = UiStrings.TheReportPageCouldNotBeOpenedErrorCodeReportOpenFailed;
        feedback.Visibility = Visibility.Visible;
    }

    public void Dispose()
    {
        _closed = true;
        _installer.OperationError -= OperationError;
        _root.Loaded -= RootLoaded;
        _lifetime.Cancel();
        _activeDialog?.Hide();
        _pending.Clear();
        _lifetime.Dispose();
    }
}
