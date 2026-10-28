using System;
using Crimson.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Crimson.Controls;

/// <summary>An in-app overlay: it never activates a window, moves focus, or blocks the page.</summary>
public sealed partial class NotificationHost : UserControl
{
    private NotificationService? _service;
    private AppNotification? _displayed;
    private bool _subscribed;
    private bool _pointerOver;
    private bool _hasFocus;

    public NotificationHost()
    {
        InitializeComponent();
        Loaded += Host_Loaded;
        Unloaded += Host_Unloaded;
    }

    public void Bind(NotificationService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        Detach();
        _service = service;
        if (IsLoaded) Attach();
    }

    private void Host_Loaded(object sender, RoutedEventArgs e) => Attach();
    private void Host_Unloaded(object sender, RoutedEventArgs e) => Detach();

    private void Attach()
    {
        if (_service is null || _subscribed) return;
        _service.Changed += NotificationChanged;
        _subscribed = true;
        Apply(_service.Current);
    }

    private void Detach()
    {
        if (_service is not null && _subscribed)
        {
            _service.Changed -= NotificationChanged;
            if (_displayed is not null) _service.Resume(_displayed);
        }
        _subscribed = false;
        _pointerOver = false;
        _hasFocus = false;
    }

    private void NotificationChanged(AppNotification? notification)
    {
        var service = _service;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_subscribed && ReferenceEquals(service, _service) && ReferenceEquals(notification, service?.Current))
                Apply(notification);
        });
    }

    private void Apply(AppNotification? notification)
    {
        _displayed = notification;
        if (notification is not null)
        {
            NotificationBar.Title = notification.Title;
            NotificationBar.Message = notification.Message;
            NotificationBar.Severity = notification.Severity switch
            {
                NotificationSeverity.Success => InfoBarSeverity.Success,
                NotificationSeverity.Warning => InfoBarSeverity.Warning,
                NotificationSeverity.Error => InfoBarSeverity.Error,
                _ => InfoBarSeverity.Informational
            };
        }
        NotificationBar.IsOpen = notification is not null;
        IsHitTestVisible = notification is not null;
        UpdatePause();
    }

    private void NotificationBar_CloseButtonClick(InfoBar sender, object args)
    {
        if (_displayed is not null) _service?.Dismiss(_displayed);
    }

    private void Host_PointerEntered(object sender, PointerRoutedEventArgs e) { _pointerOver = true; UpdatePause(); }
    private void Host_PointerExited(object sender, PointerRoutedEventArgs e) { _pointerOver = false; UpdatePause(); }
    private void Host_GotFocus(object sender, RoutedEventArgs e) { _hasFocus = true; UpdatePause(); }
    private void Host_LostFocus(object sender, RoutedEventArgs e) { _hasFocus = false; UpdatePause(); }

    private void UpdatePause()
    {
        if (_displayed is null || _service is null) return;
        if (_pointerOver || _hasFocus) _service.Pause(_displayed);
        else _service.Resume(_displayed);
    }
}
