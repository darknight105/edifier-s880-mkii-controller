using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace S880Tray;

internal interface IControllerBackend
{
    string DataDirectory { get; }
    Task<JsonElement> RunAsync(params string[] command);
}

internal sealed class Backend : IControllerBackend
{
    public string DataDirectory { get; }
    public string Executable { get; }
    public string Address { get; }
    public PortablePayloadLocation Payload { get; }
    public List<string[]> Invocations { get; } = new();
    public Backend(string appDirectory, string dataDirectory)
    {
        DataDirectory = dataDirectory;
        Payload = PortablePayload.Resolve(appDirectory, dataDirectory);
        Executable = Payload.BackendExecutable;
        if (!BuildSpeakerBinding.IsConfigured)
            throw new InvalidOperationException("This build has no configured speaker address. Rebuild with -p:SpeakerAddress=AA:BB:CC:DD:EE:FF before using live controls.");
        using var document = JsonDocument.Parse(File.ReadAllText(Payload.ProfilePath));
        Address = document.RootElement.GetProperty("boundDevice").GetProperty("address").GetString() ?? "";
        if (!string.Equals(Address, BuildSpeakerBinding.Address, StringComparison.OrdinalIgnoreCase) ||
            document.RootElement.GetProperty("boundDevice").GetProperty("addressType").GetString() != "public")
            throw new InvalidOperationException("The speaker profile does not match this build's configured public speaker target. The operation was stopped.");
        Directory.CreateDirectory(DataDirectory);
    }

    public async Task<JsonElement> RunAsync(params string[] command)
    {
        Invocations.Add((string[])command.Clone());
        var output = Path.Combine(DataDirectory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".json");
        var start = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in command) start.ArgumentList.Add(argument);
        foreach (var argument in new[] { "--address", Address, "--address-type", "public", "--output", output, "--timeout-seconds", "30" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The speaker control program could not be started.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        // The CLI bounds BLE work and cleanup. Never kill an in-flight write to enforce a GUI timeout.
        await process.WaitForExitAsync();
        await Task.WhenAll(stdout, stderr);
        if (!File.Exists(output)) throw new InvalidOperationException("The control program produced no result, so the device state is unconfirmed. Refresh and check the log folder: " + DataDirectory);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(output));
        var root = document.RootElement.Clone();
        if (process.ExitCode != 0 || root.GetProperty("exitCode").GetInt32() != 0 || root.GetProperty("status").GetString() != "completed")
        {
            throw new BackendException(DescribeFailure(root, output), root, output);
        }
        return root;
    }

    internal async Task<bool> SwitchToUsbAndConfirmAsync()
    {
        Exception? writeError = null;
        try
        {
            await RunAsync("source", "usb");
        }
        catch (Exception error)
        {
            writeError = error;
        }

        try
        {
            var readback = await RunAsync("source", "get");
            var current = readback.GetProperty("result").GetProperty("currentAfter").GetProperty("name").GetString();
            if (string.Equals(current, "usb", StringComparison.OrdinalIgnoreCase)) return true;

            var detail = writeError is null
                ? "The USB switch command completed, but readback reported '" + (current ?? "unknown") + "'."
                : "Write result: " + writeError.Message + "\nReadback reported '" + (current ?? "unknown") + "'.";
            throw new InvalidOperationException(detail + "\nLogs: " + DataDirectory);
        }
        catch (Exception readbackError)
        {
            if (writeError is null)
                throw new InvalidOperationException("The USB switch could not be confirmed by readback. " + readbackError.Message + "\nLogs: " + DataDirectory, readbackError);
            throw new InvalidOperationException("The USB switch was not confirmed.\nWrite result: " + writeError.Message + "\nReadback: " + readbackError.Message + "\nLogs: " + DataDirectory, readbackError);
        }
    }

    internal static string DescribeFailure(JsonElement report, string reportPath)
    {
        if (report.TryGetProperty("status", out var status) && status.GetString() == "outcome-uncertain")
            return "The operation result is unconfirmed; some changes may have taken effect. Refresh before trying again.";

        string? discoveryError = null;
        if (report.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
        {
            foreach (var error in errors.EnumerateArray())
            {
                if (error.ValueKind != JsonValueKind.String) continue;
                var text = error.GetString();
                if (string.Equals(text, "source service discovery failed: Unreachable", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(text, "EQ service discovery failed: Unreachable", StringComparison.OrdinalIgnoreCase))
                { discoveryError = text; break; }
            }
        }
        if (discoveryError is null) return "The operation did not complete. Check the speaker and Bluetooth, then refresh.";

        var noSettingSent = false;
        if (report.TryGetProperty("safety", out var safety) && safety.ValueKind == JsonValueKind.Object &&
            safety.TryGetProperty("attemptedWritesHex", out var attempts) && attempts.ValueKind == JsonValueKind.Array && attempts.GetArrayLength() == 0)
        {
            var sourceFlag = safety.TryGetProperty("setAttempted", out var sourceAttempt);
            var eqFlag = safety.TryGetProperty("stateChangingCommandSent", out var eqAttempt);
            noSettingSent = (sourceFlag && sourceAttempt.ValueKind == JsonValueKind.False || eqFlag && eqAttempt.ValueKind == JsonValueKind.False)
                && (!sourceFlag || sourceAttempt.ValueKind == JsonValueKind.False)
                && (!eqFlag || eqAttempt.ValueKind == JsonValueKind.False);
        }
        return "Windows could not reach the speaker's Bluetooth control service. " +
            (noSettingSent ? "No setting command was sent. " : "") +
            "If EDIFIER Connect is running, force stop it in your phone settings. Switch the speaker to Bluetooth with its remote, then refresh." +
            "\nDetails: " + discoveryError + "\nReport: " + reportPath;
    }
}

internal sealed class BackendException(string message, JsonElement report, string path) : Exception(message)
{
    public JsonElement Report { get; } = report;
    public string ReportPath { get; } = path;
}
