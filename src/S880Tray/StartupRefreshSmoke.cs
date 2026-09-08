using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;

namespace S880Tray;

// Explicit --smoke coverage only. It uses fixture backends and never creates a tray icon.
internal static class StartupRefreshSmoke
{
    private static readonly string[] ReadCommands = ["source get", "eq get", "eq custom-get", "volume get"];

    internal static async Task<object> RunAsync()
    {
        var fixture = new FixtureBackend { DelayNext = true };
        var window = CreateOffscreen(fixture);
        var noReadBeforeFirstPanel = fixture.Commands.Count == 0;
        var startupLabelClear = string.Equals(window.StartupToggle.Content?.ToString(), "Start with Windows", StringComparison.Ordinal) &&
            AutomationProperties.GetHelpText(window.StartupToggle).Contains("system tray", StringComparison.OrdinalIgnoreCase) &&
            AutomationProperties.GetHelpText(window.StartupToggle).Contains("sign in", StringComparison.OrdinalIgnoreCase);

        window.ShowPanel();
        var returnedWhileReadInProgress = window.LifecycleBusy && !window.WaitForInitialRefreshAsync().IsCompleted;
        await window.WaitForInitialRefreshAsync();
        var firstPanelAutoRead = window.LastOperationSucceeded && !window.LifecycleBusy && fixture.Commands.SequenceEqual(ReadCommands);

        window.LifecycleDraft = 0.5;
        window.Hide();
        var beforeReopen = fixture.Commands.ToArray();
        window.ShowPanel();
        await window.WaitForInitialRefreshAsync();
        var reopenDoesNotReadAgain = fixture.Commands.SequenceEqual(beforeReopen);
        var reopenPreservesDraft = window.LifecycleDraft == 0.5;
        window.Hide();

        var failureFixture = new FixtureBackend { FailNext = true };
        var failureWindow = CreateOffscreen(failureFixture);
        failureWindow.ShowPanel();
        await failureWindow.WaitForInitialRefreshAsync();
        var initialFailureClearsLoading = !failureWindow.LifecycleBusy && !failureWindow.LastOperationSucceeded && failureWindow.LastError is not null;
        await failureWindow.RefreshAsync();
        var manualRefreshRecovers = failureWindow.LastOperationSucceeded && !failureWindow.LifecycleBusy && failureWindow.LastError is null &&
            failureFixture.Commands.SequenceEqual(new[] { "source get", "source get", "eq get", "eq custom-get", "volume get" });
        failureWindow.Hide();

        var raceFixture = new FixtureBackend { DelayNext = true };
        var raceWindow = CreateOffscreen(raceFixture);
        raceWindow.ShowPanel();
        var usb = raceWindow.SelectSourceUsbAsync();
        await Task.WhenAll(raceWindow.WaitForInitialRefreshAsync(), usb);
        var usbQueuesBehindInitialRead = usb.Result && raceFixture.Commands.SequenceEqual(ReadCommands.Append("source usb")) &&
            raceFixture.MaximumConcurrent == 1 && !raceWindow.LifecycleBusy;
        raceWindow.Hide();

        var passed = noReadBeforeFirstPanel && startupLabelClear && returnedWhileReadInProgress && firstPanelAutoRead &&
            reopenDoesNotReadAgain && reopenPreservesDraft && initialFailureClearsLoading && manualRefreshRecovers && usbQueuesBehindInitialRead;
        return new
        {
            passed,
            hardwareAccess = false,
            noReadBeforeFirstPanel,
            startupLabelClear,
            returnedWhileReadInProgress,
            firstPanelAutoRead,
            reopenDoesNotReadAgain,
            reopenPreservesDraft,
            initialFailureClearsLoading,
            manualRefreshRecovers,
            usbQueuesBehindInitialRead,
            firstOpenCommands = fixture.Commands,
            failureCommands = failureFixture.Commands,
            raceCommands = raceFixture.Commands,
            maximumConcurrent = raceFixture.MaximumConcurrent
        };
    }

    private static MainWindow CreateOffscreen(FixtureBackend fixture) => new(fixture, false)
    {
        WindowStartupLocation = WindowStartupLocation.Manual,
        Left = -10000,
        Top = -10000,
        ShowActivated = false,
        ShowInTaskbar = false
    };
}
