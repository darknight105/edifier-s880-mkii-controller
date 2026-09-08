using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace S880Tray;

internal static class PortablePayloadSmoke
{
    public static async Task<object> RunAsync(string appDirectory, string dataDirectory)
    {
        var backend = new Backend(appDirectory, dataDirectory);
        var payload = backend.Payload;
        var backendHash = PortablePayload.HashFile(payload.BackendExecutable);
        var profileHash = PortablePayload.HashFile(payload.ProfilePath);
        var start = new ProcessStartInfo(payload.BackendExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("--help");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The extracted backend could not be started.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await Task.WhenAll(stdout, stderr);
        var help = await stdout;
        var error = await stderr;
        var hashesMatch = backendHash.Equals(payload.BackendSha256, StringComparison.OrdinalIgnoreCase) &&
            profileHash.Equals(payload.ProfileSha256, StringComparison.OrdinalIgnoreCase);
        var cliSelfTestPassed = process.ExitCode == 0 && help.Contains("s880ctl", StringComparison.OrdinalIgnoreCase);
        var temporaryFiles = Directory.GetFiles(payload.Directory, "*.tmp", SearchOption.TopDirectoryOnly);
        var activation = await Application.Current.Dispatcher.InvokeAsync(RunActivationChecksAsync).Task.Unwrap();
        var passed = hashesMatch && cliSelfTestPassed && temporaryFiles.Length == 0 && activation.GetProperty("passed").GetBoolean();
        return new
        {
            status = passed ? "passed" : "failed",
            mode = "portable-payload-offline",
            hardwareAccess = false,
            payload.ExtractedFromEmbeddedResources,
            payload.Directory,
            backend = new { path = payload.BackendExecutable, expectedSha256 = payload.BackendSha256, actualSha256 = backendHash },
            profile = new { path = payload.ProfilePath, expectedSha256 = payload.ProfileSha256, actualSha256 = profileHash },
            cli = new { exitCode = process.ExitCode, helpRecognized = cliSelfTestPassed, stderr = error },
            hashValidationPassed = hashesMatch,
            atomicExtractionTemporaryFiles = temporaryFiles,
            activation,
            internalCacheReason = "The controller keeps the user-facing distribution to one EXE; Windows requires the embedded CLI to be materialized before it can run as a separate safety process. The directory is scoped by backend and profile hashes.",
            adjacentPayloadFilesRequired = false
        };
    }

    private static async Task<JsonElement> RunActivationChecksAsync()
    {
        var fixture = new FixtureBackend();
        var window = new MainWindow(fixture, false);
        var newAppName = "S880Controller-PayloadSmoke-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
        using var activation = new InstanceActivation(newAppName, window);
        await Task.Delay(100);
        var newAppResult = await InstanceActivation.SwitchUsbExistingAsync(newAppName);
        var newAppPassed = newAppResult.Connected && newAppResult.Succeeded && fixture.Commands.SequenceEqual(new[] { "source usb" });

        var oldAppName = "S880Controller-OldPayloadSmoke-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
        using var legacyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var oldAppServer = RunLegacySwitchServerAsync(oldAppName, legacyTimeout.Token);
        var oldAppResult = await InstanceActivation.SwitchUsbExistingAsync(oldAppName);
        if (!oldAppResult.Connected) legacyTimeout.Cancel();
        string? legacyCommand = null;
        string? legacyServerError = null;
        try { legacyCommand = await oldAppServer; }
        catch (OperationCanceledException) { legacyServerError = "The isolated legacy client did not connect before cancellation; no server wait was left running."; }
        var oldAppPassed = oldAppResult.Connected && !oldAppResult.Succeeded &&
            legacyCommand == "switch-usb" && !string.IsNullOrWhiteSpace(oldAppResult.Error);

        return JsonSerializer.SerializeToElement(new
        {
            passed = newAppPassed && oldAppPassed,
            hardwareAccess = false,
            newApp = new { newAppResult.Connected, newAppResult.Succeeded, newAppResult.Error, commands = fixture.Commands },
            oldAppUnsupported = new { oldAppResult.Connected, oldAppResult.Succeeded, oldAppResult.Error, legacyCommand, legacyServerError }
        });
    }

    private static async Task<string?> RunLegacySwitchServerAsync(string name, CancellationToken cancellationToken)
    {
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await server.WaitForConnectionAsync(cancellationToken);
        using var reader = new StreamReader(server);
        using var writer = new StreamWriter(server) { AutoFlush = true };
        await writer.WriteLineAsync(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var command = await reader.ReadLineAsync(cancellationToken);
        // A pre-command-version server closes without an acknowledgement.
        return command;
    }
}
