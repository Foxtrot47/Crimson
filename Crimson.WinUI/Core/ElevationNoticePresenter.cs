using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Serilog;

namespace Crimson.Core;

/// <summary>
/// A popup rather than a ContentDialog: move, repair and uninstall start from the game properties dialog, which
/// stays open, and only one ContentDialog can be shown at a time.
/// </summary>
internal sealed class ElevationNoticePresenter
{
    private const int CountdownSeconds = 3;

    private readonly FrameworkElement _root;
    private readonly ILogger _log;
    private readonly Func<bool> _isWindowVisible;
    private readonly Action _ensureWindowVisible;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ElevationNoticePresenter(FrameworkElement root, ElevationNotice notice, ILogger log,
        Func<bool> isWindowVisible, Action ensureWindowVisible)
    {
        _root = root;
        _log = log;
        _isWindowVisible = isWindowVisible;
        _ensureWindowVisible = ensureWindowVisible;
        notice.Presenter = RunAsync;
    }

    private async Task RunAsync(IReadOnlyList<string> reasons, Func<ElevationOutcome> elevate, CancellationToken ct)
    {
        // One notice at a time: each is followed by its own prompt.
        await _gate.WaitAsync(ct);
        try
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_root.DispatcherQueue.TryEnqueue(() => Show(reasons, elevate, completion, ct)))
            {
                elevate();
                return;
            }
            await completion.Task;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Show(IReadOnlyList<string> reasons, Func<ElevationOutcome> elevate, TaskCompletionSource completion,
        CancellationToken ct)
    {
        var notice = new Notice(this, elevate, completion, ct);
        try
        {
            if (ct.IsCancellationRequested)
            {
                completion.TrySetCanceled(ct);
                return;
            }
            if (!_isWindowVisible() || _root.XamlRoot is null)
                _ensureWindowVisible();
            if (_root.XamlRoot is not { } root)
            {
                // Nowhere to explain it: the prompt opens once, directly.
                ElevateDirectly(elevate, completion);
                return;
            }
            notice.Open(root, reasons);
        }
        catch (Exception error)
        {
            // The notice only explains the prompt; failing to show it must not block the operation.
            _log.Error(error, "Elevation notice could not be shown");
            notice.Close(() => { });
            ElevateDirectly(elevate, completion);
        }
    }

    private static void ElevateDirectly(Func<ElevationOutcome> elevate, TaskCompletionSource completion) =>
        Task.Run(elevate).ContinueWith(_ => completion.TrySetResult(), TaskScheduler.Default);

    private sealed class Notice(ElevationNoticePresenter owner, Func<ElevationOutcome> elevate,
        TaskCompletionSource completion, CancellationToken ct)
    {
        private Popup? _popup;
        private XamlRoot? _xamlRoot;
        private ElevationNoticeView? _view;
        private CancellationTokenRegistration _cancellation;
        private bool _waiting;

        internal void Open(XamlRoot root, IReadOnlyList<string> reasons)
        {
            _xamlRoot = root;
            _view = new ElevationNoticeView(reasons, CountdownSeconds) { RequestedTheme = owner._root.ActualTheme };
            _popup = new Popup { XamlRoot = root, Child = _view };
            Fit(root, null);
            root.Changed += Fit;
            _view.ContinueRequested += Elevate;
            _view.CancelRequested += () => { if (!_waiting) Close(() => completion.TrySetResult()); };
            _popup.IsOpen = true;
            _cancellation = ct.Register(() => owner._root.DispatcherQueue.TryEnqueue(() =>
            {
                // An open prompt is answered first; its outcome decides.
                if (!_waiting) Close(() => completion.TrySetCanceled(ct));
            }));
        }

        internal void Close(Action complete)
        {
            if (_popup is not { IsOpen: true }) return;
            _cancellation.Dispose();
            _xamlRoot!.Changed -= Fit;
            _popup.IsOpen = false;
            complete();
        }

        private void Fit(XamlRoot sender, XamlRootChangedEventArgs? _)
        {
            _view!.Width = sender.Size.Width;
            _view.Height = sender.Size.Height;
        }

        private async void Elevate()
        {
            if (_waiting) return;
            _waiting = true;
            _view!.ShowWaiting();
            ElevationOutcome outcome;
            try
            {
                // Opening the prompt blocks until the user answers it.
                outcome = await Task.Run(elevate);
            }
            catch (Exception error)
            {
                owner._log.Error(error, "Administrator prompt could not be opened");
                outcome = ElevationOutcome.Failed;
            }
            _waiting = false;
            if (outcome == ElevationOutcome.Approved) Close(() => completion.TrySetResult());
            else if (ct.IsCancellationRequested) Close(() => completion.TrySetCanceled(ct));
            else _view.ShowNotApproved(outcome == ElevationOutcome.Declined);
        }
    }
}
