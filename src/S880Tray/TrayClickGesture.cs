using System;
using System.Threading;

namespace S880Tray;

internal enum TrayPointerButton
{
    Left,
    Right,
    Other
}

internal interface ITrayGestureScheduler
{
    IDisposable Schedule(TimeSpan delay, Action callback);
}

internal sealed class TimerTrayGestureScheduler : ITrayGestureScheduler
{
    public IDisposable Schedule(TimeSpan delay, Action callback)
    {
        Timer? timer = null;
        timer = new Timer(_ =>
        {
            timer?.Dispose();
            callback();
        }, null, delay, Timeout.InfiniteTimeSpan);
        return timer;
    }
}

internal sealed class TrayClickGesture : IDisposable
{
    private readonly object _sync = new();
    private readonly ITrayGestureScheduler _scheduler;
    private readonly TimeSpan _singleClickDelay;
    private readonly Action<long> _singleClick;
    private readonly Action _doubleClick;
    private IDisposable? _pendingSingle;
    private long _generation;
    private bool _disposed;

    internal TrayClickGesture(ITrayGestureScheduler scheduler, TimeSpan singleClickDelay, Action<long> singleClick, Action doubleClick)
    {
        _scheduler = scheduler;
        _singleClickDelay = singleClickDelay;
        _singleClick = singleClick;
        _doubleClick = doubleClick;
    }

    internal void Click(TrayPointerButton button)
    {
        if (button != TrayPointerButton.Left) return;
        lock (_sync)
        {
            if (_disposed) return;
            CancelPendingLocked();
            var generation = ++_generation;
            _pendingSingle = _scheduler.Schedule(_singleClickDelay, () => CompleteSingleClick(generation));
        }
    }

    internal void DoubleClick(TrayPointerButton button)
    {
        if (button != TrayPointerButton.Left) return;
        lock (_sync)
        {
            if (_disposed) return;
            CancelPendingLocked();
            _generation++;
        }
        _doubleClick();
    }

    private void CompleteSingleClick(long generation)
    {
        lock (_sync)
        {
            if (_disposed || generation != _generation) return;
            _pendingSingle?.Dispose();
            _pendingSingle = null;
        }
        _singleClick(generation);
    }

    internal bool IsCurrent(long generation)
    {
        lock (_sync) return !_disposed && generation == _generation;
    }

    private void CancelPendingLocked()
    {
        _pendingSingle?.Dispose();
        _pendingSingle = null;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _generation++;
            CancelPendingLocked();
        }
    }
}
