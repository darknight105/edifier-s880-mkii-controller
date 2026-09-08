using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace S880Tray;

internal sealed class InstanceActivation : IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    internal string State { get; private set; } = "starting";
    internal string? Error { get; private set; }
    internal readonly record struct CommandResult(bool Connected, bool Succeeded, string? Error);

    internal static string MutexName(string? testSession) =>
        "Local\\EdifierS880Controller" + (testSession is null ? "" : "-test-" + testSession);

    internal static string Name(string? testSession)
    {
        var user = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Unable to identify the current Windows user.");
        return $"S880Controller-{user}-{Process.GetCurrentProcess().SessionId}" + (testSession is null ? "" : "-test-" + testSession);
    }

    internal InstanceActivation(string name, MainWindow window) => _ = ListenAsync(name, window);

    private async Task ListenAsync(string name, MainWindow window)
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                State = "waiting";
                await pipe.WaitForConnectionAsync(_shutdown.Token);
                State = "connected";
                using var clientTimeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                clientTimeout.CancelAfter(TimeSpan.FromSeconds(4));
                using var reader = new StreamReader(pipe);
                using var writer = new StreamWriter(pipe) { AutoFlush = true };
                await writer.WriteLineAsync(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var command = await reader.ReadLineAsync(clientTimeout.Token);
                if (command == "activate")
                {
                    await window.Dispatcher.InvokeAsync(window.ShowPanel);
                    State = "shown";
                    await writer.WriteLineAsync("shown");
                }
                else if (command == "switch-usb")
                {
                    var succeeded = false;
                    string detail;
                    try
                    {
                        var operation = window.Dispatcher.InvokeAsync(() => window.SelectSourceUsbAsync());
                        succeeded = await operation.Task.Unwrap();
                        detail = succeeded ? "" : window.LastError ?? "The running controller did not confirm the USB switch.";
                    }
                    catch (Exception error)
                    {
                        detail = error.Message;
                    }
                    var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(detail));
                    await writer.WriteLineAsync($"switch-usb:{(succeeded ? "ok" : "failed")}:{encoded}");
                }
                else
                {
                    await writer.WriteLineAsync("unsupported");
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (Exception error) { Error = error.GetType().Name + ": " + error.Message; State = "failed"; return; }
        }
    }

    internal static async Task<bool> ActivateExistingAsync(string name)
    {
        // ConnectAsync waits for the first instance to finish creating its window/server.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token);
            using var reader = new StreamReader(pipe);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            var primaryText = await reader.ReadLineAsync(timeout.Token);
            if (!int.TryParse(primaryText, out var primaryPid)) return false;
            NativeWindow.AllowSetForegroundWindow(primaryPid);
            await writer.WriteLineAsync("activate");
            return await reader.ReadLineAsync(timeout.Token) == "shown";
        }
        catch (OperationCanceledException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    internal static async Task<CommandResult> SwitchUsbExistingAsync(string name)
    {
        var connected = false;
        using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(connectTimeout.Token);
            connected = true;
            using var reader = new StreamReader(pipe);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            var primaryText = await reader.ReadLineAsync(connectTimeout.Token);
            if (!int.TryParse(primaryText, out var primaryPid))
                return new(true, false, "The running controller returned an invalid process identifier.");
            await writer.WriteLineAsync("switch-usb");
            using var commandTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var response = await reader.ReadLineAsync(commandTimeout.Token);
            if (response is null) return new(true, false, "The running controller closed the USB request without confirming it. It may be an older build that does not support this shortcut; close or update it before retrying. No second BLE process was started.");
            var parts = response.Split(':', 3);
            if (parts.Length != 3 || parts[0] != "switch-usb")
                return new(true, false, "The running controller does not support the USB shortcut. Close or update it before retrying.");
            var detail = DecodeError(parts[2]);
            return new(true, parts[1] == "ok", string.IsNullOrWhiteSpace(detail) ? null : detail);
        }
        catch (OperationCanceledException)
        {
            return new(connected, false, connected
                ? "The USB shortcut result is unconfirmed and the operation may still complete. Do not retry yet; refresh before retrying. No second BLE process was started."
                : null);
        }
        catch (IOException error)
        {
            return new(connected, false, connected ? "The running controller disconnected before confirming the USB switch: " + error.Message : null);
        }
        catch (UnauthorizedAccessException error)
        {
            return new(connected, false, connected ? "The running controller rejected the USB command: " + error.Message : null);
        }
    }

    private static string DecodeError(string encoded)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(encoded)); }
        catch (FormatException) { return "The running controller returned an invalid USB command response."; }
    }

    public void Dispose() { _shutdown.Cancel(); }
}
