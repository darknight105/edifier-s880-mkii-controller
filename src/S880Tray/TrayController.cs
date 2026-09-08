using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace S880Tray;

internal sealed class TrayController : IDisposable
{
    private static readonly TimeSpan ClickMargin = TimeSpan.FromMilliseconds(50);
    private readonly Dispatcher _dispatcher;
    private readonly Func<Task<bool>> _selectUsbAsync;
    private readonly Action _showPanel;
    private readonly Func<string?> _lastError;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly System.Drawing.Icon _icon;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly TrayClickGesture _gesture;
    private bool _usbRunning;
    private bool _disposed;

    internal TrayController(
        Dispatcher dispatcher,
        Forms.ContextMenuStrip menu,
        Func<Task<bool>> selectUsbAsync,
        Action showPanel,
        Func<string?> lastError)
    {
        _dispatcher = dispatcher;
        _menu = menu;
        _selectUsbAsync = selectUsbAsync;
        _showPanel = showPanel;
        _lastError = lastError;

        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/s880-controller.ico"))
            ?? throw new InvalidOperationException("The tray icon resource is unavailable.");
        using (resource.Stream)
        using (var loaded = new System.Drawing.Icon(resource.Stream))
            _icon = (System.Drawing.Icon)loaded.Clone();

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "S880 MKII · Speaker Control",
            Icon = _icon,
            ContextMenuStrip = _menu,
            Visible = true
        };
        _gesture = new TrayClickGesture(
            new TimerTrayGestureScheduler(),
            TimeSpan.FromMilliseconds(Forms.SystemInformation.DoubleClickTime) + ClickMargin,
            QueueUsbSelection,
            QueueShowPanel);
        _notifyIcon.MouseClick += TrayMouseClick;
        _notifyIcon.MouseDoubleClick += TrayMouseDoubleClick;
    }

    internal void ShowError(string message)
    {
        if (_disposed || string.IsNullOrWhiteSpace(message)) return;
        _notifyIcon.ShowBalloonTip(5000, "S880 operation incomplete", message, Forms.ToolTipIcon.Warning);
    }

    private void TrayMouseClick(object? sender, Forms.MouseEventArgs args) =>
        _gesture.Click(ToPointerButton(args.Button));

    private void TrayMouseDoubleClick(object? sender, Forms.MouseEventArgs args) =>
        _gesture.DoubleClick(ToPointerButton(args.Button));

    private static TrayPointerButton ToPointerButton(Forms.MouseButtons button) => button switch
    {
        Forms.MouseButtons.Left => TrayPointerButton.Left,
        Forms.MouseButtons.Right => TrayPointerButton.Right,
        _ => TrayPointerButton.Other
    };

    private void QueueUsbSelection(long generation)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_disposed && _gesture.IsCurrent(generation)) _ = SelectUsbSafelyAsync();
        }), DispatcherPriority.Normal);
    }

    private async Task SelectUsbSafelyAsync()
    {
        if (_disposed || _usbRunning) return;
        _usbRunning = true;
        try
        {
            if (!await _selectUsbAsync() && string.IsNullOrWhiteSpace(_lastError()))
                ShowError("USB was not confirmed. Open the control panel, refresh, and check the operation details.");
        }
        catch (Exception error)
        {
            ShowError(error.Message);
        }
        finally
        {
            _usbRunning = false;
        }
    }

    private void QueueShowPanel()
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_disposed) _showPanel();
        }), DispatcherPriority.Normal);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gesture.Dispose();
        _notifyIcon.MouseClick -= TrayMouseClick;
        _notifyIcon.MouseDoubleClick -= TrayMouseDoubleClick;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
    }
}
