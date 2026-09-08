using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace S880Tray;

internal static class PortableActivationClient
{
    public static async Task<bool> ActivateExistingAsync(string name)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (EventWaitHandle.TryOpenExisting(name + "-activate", out var request) &&
                EventWaitHandle.TryOpenExisting(name + "-shown", out var shown))
            {
                using (request)
                using (shown)
                {
                    request.Set();
                    return shown.WaitOne(TimeSpan.FromSeconds(4));
                }
            }
            await Task.Delay(50);
        }
        return false;
    }
}

internal sealed class PortableInstanceActivation : IDisposable
{
    private readonly EventWaitHandle _request;
    private readonly EventWaitHandle _shown;
    private readonly EventWaitHandle _shutdown = new(false, EventResetMode.ManualReset);
    private readonly Task _listener;

    public PortableInstanceActivation(string name, MainWindow window)
    {
        _request = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-activate");
        _shown = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-shown");
        _listener = Task.Run(() => Listen(window.Dispatcher, window.ShowPanel));
    }

    private void Listen(Dispatcher dispatcher, Action show)
    {
        var waits = new WaitHandle[] { _request, _shutdown };
        while (WaitHandle.WaitAny(waits) == 0)
        {
            dispatcher.Invoke(show);
            _shown.Set();
        }
    }

    public void Dispose()
    {
        _shutdown.Set();
        try { _listener.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _request.Dispose();
        _shown.Dispose();
        _shutdown.Dispose();
    }
}
