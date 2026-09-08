using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace S880Tray;

// Explicit --smoke fixture only. Normal startup never creates this backend.
internal sealed class FixtureBackend : IControllerBackend
{
    public string DataDirectory => "";
    public List<string> Commands { get; } = new();
    public bool FailNext { get; set; }
    public int ConnectionFailuresRemaining { get; set; }
    public bool DelayNext { get; set; }
    public bool UnsupportedCustom { get; set; }
    public int MaximumConcurrent { get; private set; }
    private int _active;
    private readonly decimal[] _gains = new decimal[6];
    private string _preset = "classic";
    private string _source = "bluetooth";
    private int _volume = 12;
    internal int VolumeForTest { set => _volume = value; }
    public async Task<JsonElement> RunAsync(params string[] command)
    {
        Commands.Add(string.Join(" ", command));
        MaximumConcurrent = Math.Max(MaximumConcurrent, ++_active);
        try
        {
            if (DelayNext) { DelayNext = false; await Task.Delay(30); }
            if (FailNext) { FailNext = false; throw new InvalidOperationException("Offline smoke test: simulated unconfirmed result"); }
            if (ConnectionFailuresRemaining > 0)
            {
                ConnectionFailuresRemaining--;
                var report = JsonSerializer.SerializeToElement(new
                {
                    status = "error",
                    exitCode = 1,
                    errorCategory = "connection",
                    retryable = true,
                    failureKind = "target-advertisement-timeout",
                    stage = "target-discovery",
                    connection = new { stage = "target-discovery", readyState = "target-not-observed", message = "Offline smoke test: target not observed" },
                    errors = new[] { "target discovery did not observe the configured speaker" },
                    safety = new { setAttempted = false, stateChangingCommandSent = false, attemptedWritesHex = Array.Empty<string>() }
                });
                const string path = "offline-connection-report.json";
                throw new BackendException(Backend.DescribeFailure(report, path), report, path);
            }
            object result;
            if (command[0] == "source") { if (command[1] != "get") _source = command[1]; result = new { currentAfter = new { name = _source } }; }
            else if (command[0] == "volume")
            {
                var before = _volume;
                if (command[1] == "set") _volume = int.Parse(command[2], CultureInfo.InvariantCulture);
                result = new { maximum = 30, currentBefore = before, currentAfter = _volume };
            }
            else if (command[1] == "get") result = new { presetName = _preset };
            else if (command[1] == "set") { _preset = command[2]; result = new { currentAfter = new { name = _preset } }; }
            else if (command[1] == "custom-get") result = new { semanticDecoded = !UnsupportedCustom, customEq = UnsupportedCustom ? null : (object)new { bands = Bands() } };
            else if (command[1] == "custom-set")
            {
                _gains[int.Parse(command[3], CultureInfo.InvariantCulture) - 1] = decimal.Parse(command[5], CultureInfo.InvariantCulture);
                _preset = "custom";
                result = new { after = new { bands = Bands() }, presetAfter = new { name = "custom" } };
            }
            else throw new InvalidOperationException("Unexpected fixture command");
            return JsonSerializer.SerializeToElement(new { status = "completed", exitCode = 0, completedAtUtc = DateTimeOffset.UtcNow, result });
        }
        finally { _active--; }
    }
    private object[] Bands() => _gains.Select((gain, index) => (object)new { band = index + 1, gainDb = gain }).ToArray();

    internal static object CheckFailureMessages()
    {
        const string log = "offline-report.json";
        var source = JsonSerializer.SerializeToElement(new { status = "error", errorCategory = "connection", retryable = true, failureKind = "discovery-failed", stage = "ready", connection = new { message = "GATT discovery failed" }, errors = new[] { "source service discovery failed: Unreachable" }, safety = new { setAttempted = false, attemptedWritesHex = Array.Empty<string>() } });
        var eq = JsonSerializer.SerializeToElement(new { status = "error", errors = new[] { "EQ service discovery failed: Unreachable" }, safety = new { stateChangingCommandSent = false, attemptedWritesHex = Array.Empty<string>() } });
        var uncertain = JsonSerializer.SerializeToElement(new { status = "outcome-uncertain", errorCategory = "connection", retryable = true, errors = new[] { "source service discovery failed: Unreachable" }, safety = new { setAttempted = false, attemptedWritesHex = Array.Empty<string>() } });
        var cancelled = JsonSerializer.SerializeToElement(new { status = "error", errorCategory = "cancelled", retryable = false, failureKind = "request-cancelled", stage = "target-discovery" });
        var unsupported = JsonSerializer.SerializeToElement(new { status = "error", errorCategory = "unsupported", retryable = false, failureKind = "low-energy-unsupported", stage = "adapter-readiness" });
        var missingSafety = JsonSerializer.SerializeToElement(new { status = "error", errors = new[] { "source service discovery failed: Unreachable" } });
        var contradictory = JsonSerializer.SerializeToElement(new { status = "error", errors = new[] { "source service discovery failed: Unreachable" }, safety = new { setAttempted = true, stateChangingCommandSent = false, attemptedWritesHex = Array.Empty<string>() } });
        var other = JsonSerializer.SerializeToElement(new { status = "protocol-rejected", errors = new[] { "product identity mismatch" } });
        var sourceMessage = Backend.DescribeFailure(source, log);
        var eqMessage = Backend.DescribeFailure(eq, log);
        var uncertainMessage = Backend.DescribeFailure(uncertain, log);
        var discoveryStageAndNextStep = sourceMessage.Contains("Bluetooth control service", StringComparison.Ordinal) && sourceMessage.Contains("Keep the speaker powered on and nearby", StringComparison.Ordinal) && sourceMessage.Contains("ready · discovery-failed · GATT discovery failed", StringComparison.Ordinal) && sourceMessage.Contains(log, StringComparison.Ordinal);
        var explicitSafetyEvidence = sourceMessage.Contains("No setting command was sent.", StringComparison.Ordinal) && eqMessage.Contains("No setting command was sent.", StringComparison.Ordinal);
        var noUnsupportedSafetyClaim = !Backend.DescribeFailure(missingSafety, log).Contains("No setting", StringComparison.Ordinal) && !Backend.DescribeFailure(contradictory, log).Contains("No setting", StringComparison.Ordinal);
        var uncertainTakesPrecedence = uncertainMessage.StartsWith("The operation result is unconfirmed;", StringComparison.Ordinal) && !uncertainMessage.Contains("Switch the speaker", StringComparison.Ordinal) && !uncertainMessage.Contains("No setting", StringComparison.Ordinal);
        var otherFailuresUnchanged = Backend.DescribeFailure(other, log) == "The operation did not complete. Check the speaker and Bluetooth, then refresh.";
        var structuredRetryClassification = Backend.ShouldRetryRead(source) && !Backend.ShouldRetryRead(uncertain) && !Backend.ShouldRetryRead(cancelled) && !Backend.ShouldRetryRead(unsupported) && !Backend.ShouldRetryRead(other);
        return new { passed = discoveryStageAndNextStep && explicitSafetyEvidence && noUnsupportedSafetyClaim && uncertainTakesPrecedence && otherFailuresUnchanged && structuredRetryClassification, discoveryStageAndNextStep, explicitSafetyEvidence, noUnsupportedSafetyClaim, uncertainTakesPrecedence, otherFailuresUnchanged, structuredRetryClassification, sourceMessage, eqMessage, uncertainMessage };
    }

    internal static object CheckThemeStorage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "S880-theme-smoke-" + Guid.NewGuid().ToString("N"));
        var store = new UserSettingsStore(directory);
        var defaultLight = !store.ReadDarkTheme();
        Directory.CreateDirectory(directory); File.WriteAllText(store.PathName, "{\"unrelated\":\"keep\"}");
        store.SaveDarkTheme(true); var darkReloaded = new UserSettingsStore(directory).ReadDarkTheme();
        using var saved = JsonDocument.Parse(File.ReadAllText(store.PathName));
        var unrelatedPreserved = saved.RootElement.GetProperty("unrelated").GetString() == "keep";
        store.SaveDarkTheme(false); var lightReloaded = !new UserSettingsStore(directory).ReadDarkTheme();
        var atomicTempsCleared = Directory.GetFiles(directory, "*.tmp").Length == 0;
        var blocked = Path.Combine(directory, "blocked"); File.WriteAllText(blocked, "fixture");
        var writeFailureReported = false;
        try { new UserSettingsStore(blocked).SaveDarkTheme(true); } catch (IOException) { writeFailureReported = true; }
        return new { passed = defaultLight && darkReloaded && lightReloaded && unrelatedPreserved && atomicTempsCleared && writeFailureReported, defaultLight, darkReloaded, lightReloaded, unrelatedPreserved, atomicTempsCleared, writeFailureReported, isolatedTestDirectory = directory, realUserSettingsTouched = false };
    }
}
