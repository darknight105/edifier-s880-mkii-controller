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
    private static readonly TimeSpan[] FastRecoveryDelays = [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)];

    internal static async Task<object> RunAsync()
    {
        var fixture = new FixtureBackend { DelayNext = true };
        var window = CreateOffscreen(fixture);
        var noReadBeforeFirstPanel = fixture.Commands.Count == 0;
        var resumeBeforeFirstPanelIsQuiet = !window.HandleSystemResume() && fixture.Commands.Count == 0;
        var startupLabelClear = string.Equals(window.StartupToggle.Content?.ToString(), "Start with Windows", StringComparison.Ordinal) &&
            AutomationProperties.GetHelpText(window.StartupToggle).Contains("system tray", StringComparison.OrdinalIgnoreCase) &&
            AutomationProperties.GetHelpText(window.StartupToggle).Contains("sign in", StringComparison.OrdinalIgnoreCase);

        window.ShowPanel();
        var returnedWhileReadInProgress = window.LifecycleBusy && window.LifecycleConnection == "Finding speaker" && !window.WaitForInitialRefreshAsync().IsCompleted;
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

        var autoFixture = new FixtureBackend { ConnectionFailuresRemaining = 1 };
        var autoWindow = CreateOffscreen(autoFixture, FastRecoveryDelays);
        autoWindow.ShowPanel();
        await autoWindow.WaitForInitialRefreshAsync();
        await autoWindow.WaitForRecoveryAsync();
        var failureThenAutomaticSuccess = autoWindow.LastOperationSucceeded && autoWindow.LastError is null && autoWindow.LifecycleConnection == "Ready" &&
            autoFixture.Commands.SequenceEqual(new[] { "source get", "source get", "eq get", "eq custom-get", "volume get" }) && autoFixture.MaximumConcurrent == 1;
        autoWindow.Hide();

        var exhaustionFixture = new FixtureBackend { ConnectionFailuresRemaining = 4 };
        var exhaustionWindow = CreateOffscreen(exhaustionFixture, FastRecoveryDelays);
        exhaustionWindow.ShowPanel();
        await exhaustionWindow.WaitForInitialRefreshAsync();
        await exhaustionWindow.WaitForRecoveryAsync();
        var retryExhaustionStops = !exhaustionWindow.LastOperationSucceeded && exhaustionWindow.LastError is not null && exhaustionWindow.LifecycleConnection == "Unavailable" &&
            exhaustionWindow.MessageText.Text.Contains("Could not reach the speaker", StringComparison.Ordinal) &&
            exhaustionWindow.MessageText.Text.Contains("No setting command was replayed", StringComparison.Ordinal) &&
            !exhaustionWindow.MessageText.Text.Contains("phone", StringComparison.OrdinalIgnoreCase) &&
            exhaustionFixture.Commands.SequenceEqual(Enumerable.Repeat("source get", 4)) && exhaustionFixture.MaximumConcurrent == 1;
        exhaustionWindow.Hide();

        var cancellationFixture = new FixtureBackend { ConnectionFailuresRemaining = 1 };
        var cancellationWindow = CreateOffscreen(cancellationFixture, [TimeSpan.FromMilliseconds(250)]);
        cancellationWindow.ShowPanel();
        await cancellationWindow.WaitForInitialRefreshAsync();
        var pendingBeforeManualRefresh = cancellationWindow.LifecycleConnection == "Retrying";
        var cancelledRecovery = cancellationWindow.WaitForRecoveryAsync();
        await cancellationWindow.RefreshAsync();
        await cancelledRecovery;
        await Task.Delay(280);
        var manualRefreshCancelsPendingRetry = pendingBeforeManualRefresh && cancellationWindow.LastOperationSucceeded &&
            cancellationFixture.Commands.SequenceEqual(new[] { "source get", "source get", "eq get", "eq custom-get", "volume get" });
        cancellationWindow.Hide();

        var exitFixture = new FixtureBackend { ConnectionFailuresRemaining = 1 };
        var exitWindow = CreateOffscreen(exitFixture, [TimeSpan.FromMilliseconds(250)]);
        exitWindow.ShowPanel();
        await exitWindow.WaitForInitialRefreshAsync();
        await exitWindow.RequestExitAsync(shutdownApplication: false);
        await Task.Delay(280);
        var exitCancelsPendingRetry = exitFixture.Commands.SequenceEqual(new[] { "source get" });

        var draftFixture = new FixtureBackend();
        var draftWindow = CreateOffscreen(draftFixture, FastRecoveryDelays);
        await draftWindow.RefreshAsync();
        draftWindow.LifecycleDraft = 0.5;
        draftFixture.ConnectionFailuresRemaining = 1;
        await draftWindow.RefreshAsync();
        await draftWindow.WaitForRecoveryAsync();
        var automaticReadPreservesDraft = draftWindow.LastOperationSucceeded && draftWindow.LifecycleDraft == 0.5 &&
            draftFixture.Commands.Count(command => command.Contains(" set ", StringComparison.Ordinal) || command.EndsWith(" set", StringComparison.Ordinal)) == 0;
        draftWindow.Hide();

        var writeFixture = new FixtureBackend { ConnectionFailuresRemaining = 1 };
        var writeWindow = CreateOffscreen(writeFixture, FastRecoveryDelays);
        var failedWrite = await writeWindow.SelectSourceUsbAsync();
        await writeWindow.WaitForRecoveryAsync();
        var noSetReplayAfterWriteFailure = !failedWrite && writeWindow.LastOperationSucceeded &&
            writeWindow.MessageText.Text.Contains("No setting was retried", StringComparison.Ordinal) &&
            writeFixture.Commands.SequenceEqual(new[] { "source usb", "source get", "eq get", "eq custom-get", "volume get" }) &&
            writeFixture.Commands.Count(command => command == "source usb") == 1;
        writeWindow.Hide();

        var resumeFixture = new FixtureBackend();
        var resumeWindow = CreateOffscreen(resumeFixture, FastRecoveryDelays, TimeSpan.FromMilliseconds(10));
        resumeWindow.ShowPanel();
        await resumeWindow.WaitForInitialRefreshAsync();
        resumeWindow.LifecycleDraft = 0.5;
        resumeFixture.DelayNext = true;
        var operationBeforeResume = resumeWindow.SelectSourceUsbAsync();
        var firstResumeAccepted = resumeWindow.HandleSystemResume();
        var duplicateResumeCoalesced = resumeWindow.HandleSystemResume();
        await operationBeforeResume;
        await resumeWindow.WaitForRecoveryAsync();
        var resumeQueuesOneReadAfterBusy = firstResumeAccepted && duplicateResumeCoalesced && resumeWindow.LastOperationSucceeded &&
            resumeWindow.LifecycleConnection == "Ready" && resumeWindow.LifecycleDraft == 0.5 && resumeFixture.MaximumConcurrent == 1 &&
            resumeFixture.Commands.SequenceEqual(ReadCommands.Append("source usb").Concat(ReadCommands));
        resumeWindow.Hide();

        var passed = noReadBeforeFirstPanel && resumeBeforeFirstPanelIsQuiet && startupLabelClear && returnedWhileReadInProgress && firstPanelAutoRead &&
            reopenDoesNotReadAgain && reopenPreservesDraft && initialFailureClearsLoading && manualRefreshRecovers && usbQueuesBehindInitialRead &&
            failureThenAutomaticSuccess && retryExhaustionStops && manualRefreshCancelsPendingRetry && exitCancelsPendingRetry && automaticReadPreservesDraft && noSetReplayAfterWriteFailure && resumeQueuesOneReadAfterBusy;
        return new
        {
            passed,
            hardwareAccess = false,
            noReadBeforeFirstPanel,
            resumeBeforeFirstPanelIsQuiet,
            startupLabelClear,
            returnedWhileReadInProgress,
            firstPanelAutoRead,
            reopenDoesNotReadAgain,
            reopenPreservesDraft,
            initialFailureClearsLoading,
            manualRefreshRecovers,
            usbQueuesBehindInitialRead,
            failureThenAutomaticSuccess,
            retryExhaustionStops,
            manualRefreshCancelsPendingRetry,
            exitCancelsPendingRetry,
            automaticReadPreservesDraft,
            noSetReplayAfterWriteFailure,
            resumeQueuesOneReadAfterBusy,
            firstOpenCommands = fixture.Commands,
            failureCommands = failureFixture.Commands,
            raceCommands = raceFixture.Commands,
            recoveryCommands = autoFixture.Commands,
            exhaustionCommands = exhaustionFixture.Commands,
            cancellationCommands = cancellationFixture.Commands,
            exitCommands = exitFixture.Commands,
            draftCommands = draftFixture.Commands,
            writeFailureCommands = writeFixture.Commands,
            resumeCommands = resumeFixture.Commands,
            maximumConcurrent = new { startup = raceFixture.MaximumConcurrent, recovery = autoFixture.MaximumConcurrent, exhaustion = exhaustionFixture.MaximumConcurrent, resume = resumeFixture.MaximumConcurrent }
        };
    }

    private static MainWindow CreateOffscreen(FixtureBackend fixture, TimeSpan[]? recoveryDelays = null, TimeSpan? resumeReadDelay = null) => new(fixture, false, recoveryDelays: recoveryDelays, resumeReadDelay: resumeReadDelay)
    {
        WindowStartupLocation = WindowStartupLocation.Manual,
        Left = -10000,
        Top = -10000,
        ShowActivated = false,
        ShowInTaskbar = false
    };
}
