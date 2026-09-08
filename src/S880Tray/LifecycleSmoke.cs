using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Interop;

namespace S880Tray;

// Explicit isolated lifecycle test only; never attaches to an existing production process.
internal static class LifecycleSmoke
{
    internal static async Task RunAsync(MainWindow window, string session, string path, FixtureBackend fixture, InstanceActivation? activation = null)
    {
        await window.WaitForInitialRefreshAsync();
        await Task.Delay(200);
        var handle = new WindowInteropHelper(window).Handle;
        var initiallyVisible = NativeWindow.IsWindowVisible(handle);
        var startupReadCommands = fixture.Commands.ToArray();
        var firstOpenReadOnly = window.LastOperationSucceeded && !window.LifecycleBusy &&
            startupReadCommands.SequenceEqual(new[] { "source get", "eq get", "eq custom-get", "volume get" });
        window.LifecycleDraft = 0.5;
        window.Close(); await Task.Delay(100);
        var hiddenBeforeLaunch = !NativeWindow.IsWindowVisible(handle);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        start.ArgumentList.Add("--lifecycle-secondary"); start.ArgumentList.Add(session);
        using var secondary = Process.Start(start) ?? throw new InvalidOperationException("Lifecycle secondary failed to start");
        var secondaryDialog = "";
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!secondary.HasExited && DateTime.UtcNow < deadline)
        {
            // Baseline duplicate-instance dialog is closed only for this exact child PID.
            NativeWindow.EnumWindows((candidate, _) =>
            {
                NativeWindow.GetWindowThreadProcessId(candidate, out var pid);
                if (pid == secondary.Id && NativeWindow.IsWindowVisible(candidate))
                {
                    NativeWindow.EnumChildWindows(candidate, (child, _) => { var text = new System.Text.StringBuilder(1024); NativeWindow.GetWindowText(child, text, text.Capacity); secondaryDialog += text + " "; return true; }, IntPtr.Zero);
                    NativeWindow.PostMessage(candidate, 0x0010, IntPtr.Zero, IntPtr.Zero);
                }
                return true;
            }, IntPtr.Zero);
            await Task.Delay(100);
        }
        await window.WaitForInitialRefreshAsync();
        var visibleAfter = NativeWindow.IsWindowVisible(handle);
        var draftPreserved = window.LifecycleDraft == 0.5;
        var noDuplicateReadOnReopen = fixture.Commands.SequenceEqual(startupReadCommands);
        var report = new { initiallyVisible, firstOpenReadOnly, loadingCleared = !window.LifecycleBusy, hiddenBeforeLaunch, closeToTray = hiddenBeforeLaunch, visibleAfterSecondLaunch = visibleAfter, primaryPid = Environment.ProcessId, secondaryPid = secondary.Id, secondaryExited = secondary.HasExited, secondaryExitCode = secondary.HasExited ? secondary.ExitCode : (int?)null, secondaryDialog, activationState = activation?.State, activationError = activation?.Error, samePrimaryWindow = new WindowInteropHelper(window).Handle == handle, noDuplicateReadOnReopen, draftPreserved, fixtureCommands = fixture.Commands, hardwareAccess = false, passed = initiallyVisible && firstOpenReadOnly && !window.LifecycleBusy && hiddenBeforeLaunch && visibleAfter && secondary.HasExited && secondary.ExitCode == 0 && noDuplicateReadOnReopen && draftPreserved };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}

internal static class NativeWindow
{
    internal delegate bool EnumCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] internal static extern bool EnumChildWindows(IntPtr parent, EnumCallback callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int count);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool AllowSetForegroundWindow(int processId);
}
