using System;
using System.Collections.Generic;
using Crimson.Resources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using VirtualKey = Windows.System.VirtualKey;

namespace Crimson.Controls;

public sealed partial class ElevationNoticeView : UserControl
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);

    private readonly DispatcherQueueTimer _timer;
    private readonly TimeSpan _countdown;
    private TimeSpan _remaining;
    private bool _waiting;

    public event Action? ContinueRequested;
    public event Action? CancelRequested;

    public ElevationNoticeView(IReadOnlyList<string> reasons, int seconds)
    {
        InitializeComponent();
        ReasonList.ItemsSource = reasons;
        _countdown = _remaining = TimeSpan.FromSeconds(seconds);
        ShowRemaining();
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = Tick;
        _timer.Tick += (_, _) =>
        {
            _remaining -= Tick;
            if (_remaining > TimeSpan.Zero) ShowRemaining();
            else Continue();
        };
        Loaded += (_, _) =>
        {
            _timer.Start();
            ContinueButton.Focus(FocusState.Programmatic);
        };
        KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Escape) return;
            e.Handled = true;
            Cancel();
        };
    }

    public void ShowWaiting()
    {
        _timer.Stop();
        _waiting = true;
        CountdownPanel.Visibility = Visibility.Collapsed;
        WaitingPanel.Visibility = Visibility.Visible;
        StatusBar.IsOpen = false;
        ContinueButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
    }

    public void ShowNotApproved(bool declined)
    {
        _waiting = false;
        WaitingPanel.Visibility = Visibility.Collapsed;
        StatusBar.Severity = declined ? InfoBarSeverity.Warning : InfoBarSeverity.Error;
        StatusBar.Title = declined ? UiStrings.ElevationNoticeDeclinedTitle : UiStrings.ElevationNoticeFailedTitle;
        StatusBar.IsOpen = true;
        ContinueText.Text = UiStrings.ElevationNoticeRetry;
        ContinueButton.IsEnabled = true;
        CancelButton.IsEnabled = true;
        ContinueButton.Focus(FocusState.Programmatic);
    }

    private void ShowRemaining()
    {
        CountdownText.Text = UiStrings.Format(UiStrings.ElevationNoticeCountdownFormat,
            (int)Math.Ceiling(_remaining.TotalSeconds));
        CountdownBar.Value = _remaining / _countdown;
    }

    private void ContinueClicked(object sender, RoutedEventArgs e) => Continue();

    private void CancelClicked(object sender, RoutedEventArgs e) => Cancel();

    private void Continue()
    {
        if (_waiting) return;
        _timer.Stop();
        ContinueRequested?.Invoke();
    }

    private void Cancel()
    {
        if (_waiting) return;
        _timer.Stop();
        CancelRequested?.Invoke();
    }
}
