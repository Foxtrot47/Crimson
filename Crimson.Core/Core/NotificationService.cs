using System;
using System.Threading;

namespace Crimson.Core;

public enum NotificationSeverity
{
    Information,
    Success,
    Warning,
    Error
}

public sealed record AppNotification(string Title, string Message, NotificationSeverity Severity = NotificationSeverity.Information);

/// <summary>A single transient message, shared by the shell and any feature that needs feedback.</summary>
public sealed class NotificationService : IDisposable
{
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(6);
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private AppNotification? _current;
    private ITimer? _timer;
    private long _revision;
    private TimeSpan _duration;
    private bool _paused;
    private bool _disposed;

    public NotificationService() : this(TimeProvider.System) { }

    public NotificationService(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    public event Action<AppNotification?>? Changed;
    public AppNotification? Current { get { lock (_gate) return _current; } }

    public void Show(AppNotification notification, TimeSpan? duration = null)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentException.ThrowIfNullOrWhiteSpace(notification.Title);
        var lifetime = duration ?? DefaultDuration;
        if (lifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CancelTimer();
            _current = notification;
            _duration = lifetime;
            _paused = false;
            StartTimer();
            Changed?.Invoke(_current);
        }
    }

    public void Dismiss(AppNotification? expected = null)
    {
        lock (_gate)
        {
            if (_current is null || (expected is not null && !ReferenceEquals(expected, _current))) return;
            CancelTimer();
            _current = null;
            _paused = false;
            Changed?.Invoke(null);
        }
    }

    public void Pause(AppNotification expected)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(expected, _current) || _paused) return;
            _paused = true;
            CancelTimer();
        }
    }

    public void Resume(AppNotification expected)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(expected, _current) || !_paused) return;
            _paused = false;
            StartTimer();
        }
    }

    private void StartTimer()
    {
        var revision = ++_revision;
        _timer = _timeProvider.CreateTimer(_ =>
        {
            lock (_gate)
            {
                if (revision != _revision || _paused || _disposed) return;
                Dismiss();
            }
        }, null, _duration, Timeout.InfiniteTimeSpan);
    }

    private void CancelTimer()
    {
        _revision++;
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Dismiss();
            CancelTimer();
        }
    }
}
