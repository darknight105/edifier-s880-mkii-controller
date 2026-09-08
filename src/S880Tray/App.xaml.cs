using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace S880Tray;

public partial class App : Application
{
    private Mutex? _instance;
    private InstanceActivation? _activation;
    private PortableInstanceActivation? _portableActivation;
    private bool _powerModeSubscribed;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var englishUi = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentUICulture = englishUi;
        CultureInfo.CurrentUICulture = englishUi;
        try
        {
            var options = ParseOptions(e.Args);
            var dataDirectory = options.DataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "S880Controller");
            if (options.SwitchUsb)
            {
                await RunSwitchUsbAsync(dataDirectory);
                return;
            }
            if (options.PayloadSmokePath is not null)
            {
                var path = Path.GetFullPath(options.PayloadSmokePath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                JsonElement result;
                try { result = JsonSerializer.SerializeToElement(await PortablePayloadSmoke.RunAsync(AppContext.BaseDirectory, dataDirectory)); }
                catch (Exception error)
                {
                    result = JsonSerializer.SerializeToElement(new { status = "failed", mode = "portable-payload-offline", hardwareAccess = false, errorType = error.GetType().FullName, error = error.Message, stackTrace = error.StackTrace });
                }
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                await Dispatcher.InvokeAsync(() => Shutdown(result.GetProperty("status").GetString() == "passed" ? 0 : 1)); return;
            }
            var preview = options.PreviewPath is not null || options.SmokePath is not null;
            var lifecycle = options.LifecyclePath is not null || options.LifecycleSecondary is not null;
            var lifecycleSession = options.LifecycleSecondary ?? (options.LifecyclePath is not null ? Guid.NewGuid().ToString("N") : null);
            var instanceName = InstanceActivation.Name(lifecycleSession);
            if (!preview && options.ReadSmokeDirectory is null)
            {
                var mutexName = InstanceActivation.MutexName(lifecycleSession);
                _instance = new Mutex(true, mutexName, out var firstInstance);
                if (!firstInstance)
                {
                    _instance.Dispose(); _instance = null;
                    if (options.StartInTray) { Shutdown(0); return; }
                    var activated = await PortableActivationClient.ActivateExistingAsync(instanceName) ||
                        await InstanceActivation.ActivateExistingAsync(instanceName);
                    if (!activated) MessageBox.Show("The running controller could not be reached. Please try its system tray icon.", "S880 Controller");
                    Shutdown(activated ? 0 : 1); return;
                }
            }
            Backend? backend = null; string? startupError = null;
            if (!preview && !lifecycle) { try { backend = new Backend(AppContext.BaseDirectory, dataDirectory); } catch (Exception error) { startupError = error.Message; } }
            var fixtureForLifecycle = lifecycle ? new FixtureBackend() : null;
            var window = new MainWindow(fixtureForLifecycle ?? (IControllerBackend?)backend, preview, startupError); MainWindow = window;
            if (options.PreviewPath is not null)
            {
                window.Render(options.PreviewPath); Shutdown(); return;
            }
            if (options.SmokePath is not null)
            {
                var path = Path.GetFullPath(options.SmokePath); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var image = Path.ChangeExtension(path, ".png"); window.Render(image);
                window.Width = window.MinWidth; window.Height = 540;
                var minimumImage = Path.Combine(Path.GetDirectoryName(path)!, "minimum-size.png"); window.Render(minimumImage);
                window.Width = 680;
                var embeddedPayloadAvailable = PortablePayload.HasEmbeddedPayload;
                var missingBackendRejected = false;
                if (!embeddedPayloadAvailable)
                {
                    try { _ = new Backend(Path.Combine(dataDirectory, "nonexistent-smoke-app"), dataDirectory); }
                    catch (InvalidOperationException) { missingBackendRejected = true; }
                }
                var payloadAvailabilityValidated = embeddedPayloadAvailable || missingBackendRejected;
                var fixture = new FixtureBackend();
                var checkWindow = new MainWindow(fixture, false);
                var checks = JsonSerializer.SerializeToElement(await checkWindow.RunOfflineChecksAsync(fixture));
                var startupRefreshChecks = JsonSerializer.SerializeToElement(await StartupRefreshSmoke.RunAsync());
                var trayStartupChecks = JsonSerializer.SerializeToElement(TrayStartupChecks.Run());
                var passed = payloadAvailabilityValidated && checks.GetProperty("passed").GetBoolean() && startupRefreshChecks.GetProperty("passed").GetBoolean() && trayStartupChecks.GetProperty("passed").GetBoolean();
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { status = passed ? "passed" : "failed", mode = "offline-preview", hardwareAccess = false, realInvokedCommands = Array.Empty<string>(), embeddedPayloadAvailable, missingBackendRejected, payloadAvailabilityValidated, checks, startupRefreshChecks, trayStartupChecks, preview = image, note = "Fixture is created only in explicit offline smoke mode; startup refresh uses fixture queries, tray gesture tests use a manual clock, and startup registration tests use an in-memory registry." }, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(passed ? 0 : 1); return;
            }
            if (options.ReadSmokeDirectory is not null)
            {
                var directory = Path.GetFullPath(options.ReadSmokeDirectory); Directory.CreateDirectory(directory);
                if (backend is not null) await window.RefreshAsync();
                window.Render(Path.Combine(directory, "read-smoke.png"));
                var actual = backend?.Invocations.Select(command => string.Join(" ", command)).ToArray() ?? [];
                var exactQueries = actual.SequenceEqual(new[] { "source get", "eq get", "eq custom-get", "volume get" });
                var success = window.LastOperationSucceeded && exactQueries;
                await File.WriteAllTextAsync(Path.Combine(directory, "read-smoke.json"), JsonSerializer.Serialize(new { status = success ? "passed" : "failed", mode = "read-only-live", invokedCommands = actual, exactQueries, stateChangingCommands = actual.Count(command => command is not ("source get" or "eq get" or "eq custom-get" or "volume get")), error = startupError ?? window.LastError, dataDirectory }, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(success ? 0 : 1); return;
            }
            window.Height = Math.Max(window.MinHeight, Math.Min(window.Height, SystemParameters.WorkArea.Height - 32));
            if (backend is not null)
            {
                try
                {
                    SystemEvents.PowerModeChanged += SystemPowerModeChanged;
                    _powerModeSubscribed = true;
                }
                catch (PlatformNotSupportedException) { }
            }
            _activation = new InstanceActivation(instanceName, window);
            _portableActivation = new PortableInstanceActivation(instanceName, window);
            window.CreateTray();
            if (!options.StartInTray) window.ShowPanel();
            if (options.LifecyclePath is not null)
            {
                await LifecycleSmoke.RunAsync(window, lifecycleSession!, options.LifecyclePath, fixtureForLifecycle!, _activation);
                await window.RequestExitAsync();
            }
        }
        catch (Exception error) { MessageBox.Show(error.Message, "S880 Controller could not start", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_powerModeSubscribed) { SystemEvents.PowerModeChanged -= SystemPowerModeChanged; _powerModeSubscribed = false; }
        _activation?.Dispose();
        _portableActivation?.Dispose();
        if (_instance is not null) { _instance.ReleaseMutex(); _instance.Dispose(); }
        base.OnExit(e);
    }

    private void SystemPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume || Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (MainWindow is S880Tray.MainWindow window) window.HandleSystemResume();
        }, DispatcherPriority.Background);
    }

    private async Task RunSwitchUsbAsync(string dataDirectory)
    {
        var instanceName = InstanceActivation.Name(null);
        var mutexName = InstanceActivation.MutexName(null);
        _instance = new Mutex(true, mutexName, out var firstInstance);
        if (!firstInstance)
        {
            _instance.Dispose(); _instance = null;
            var routed = await InstanceActivation.SwitchUsbExistingAsync(instanceName);
            if (!routed.Succeeded)
            {
                var message = routed.Error ?? (routed.Connected
                    ? "The running controller did not confirm the USB switch. No second BLE process was started."
                    : "The running controller could not be reached. Open it once, then try the shortcut again.");
                MessageBox.Show(message, "S880 USB shortcut", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            Shutdown(routed.Succeeded ? 0 : 1);
            return;
        }

        try
        {
            var backend = new Backend(AppContext.BaseDirectory, dataDirectory);
            var confirmed = await backend.SwitchToUsbAndConfirmAsync();
            if (!confirmed)
                throw new InvalidOperationException("The USB switch was not confirmed. Check the operation logs: " + dataDirectory);
            Shutdown(0);
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "S880 USB shortcut failed", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static Options ParseOptions(string[] args)
    {
        var result = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--switch-usb") { result.SwitchUsb = true; continue; }
            if (args[i] == "--start-in-tray") { result.StartInTray = true; continue; }
            if (i + 1 >= args.Length) throw new ArgumentException("A path is missing after the startup option.");
            var raw = args[++i];
            var value = args[i - 1] == "--lifecycle-secondary" ? raw : Path.GetFullPath(raw);
            switch (args[i - 1])
            {
                case "--data-dir": result.DataDirectory = value; break;
                case "--render-preview": result.PreviewPath = value; break;
                case "--smoke": result.SmokePath = value; break;
                case "--read-smoke": result.ReadSmokeDirectory = value; break;
                case "--payload-smoke": result.PayloadSmokePath = value; break;
                case "--lifecycle-smoke": result.LifecyclePath = value; break;
                case "--lifecycle-secondary": result.LifecycleSecondary = value; break;
                default: throw new ArgumentException("Unknown startup option: " + args[i - 1]);
            }
        }
        if (result.SwitchUsb && new[] { result.PreviewPath, result.SmokePath, result.ReadSmokeDirectory, result.PayloadSmokePath, result.LifecyclePath, result.LifecycleSecondary }.Any(path => path is not null)) throw new ArgumentException("The USB shortcut cannot be combined with another preview, smoke-test, or lifecycle option.");
        if (result.StartInTray && (result.SwitchUsb || new[] { result.PreviewPath, result.SmokePath, result.ReadSmokeDirectory, result.PayloadSmokePath, result.LifecyclePath, result.LifecycleSecondary }.Any(path => path is not null))) throw new ArgumentException("Start-in-tray cannot be combined with another command or test mode.");
        if (new[] { result.PreviewPath, result.SmokePath, result.ReadSmokeDirectory, result.PayloadSmokePath }.Count(path => path is not null) > 1) throw new ArgumentException("Specify only one preview or smoke-test mode.");
        if (result.LifecycleSecondary is not null && !Guid.TryParseExact(result.LifecycleSecondary, "N", out _)) throw new ArgumentException("Invalid lifecycle test session.");
        return result;
    }
    private sealed class Options
    {
        public string? DataDirectory { get; set; }
        public string? PreviewPath { get; set; }
        public string? SmokePath { get; set; }
        public string? ReadSmokeDirectory { get; set; }
        public string? PayloadSmokePath { get; set; }
        public bool SwitchUsb { get; set; }
        public bool StartInTray { get; set; }
        public string? LifecyclePath { get; set; }
        public string? LifecycleSecondary { get; set; }
    }
}
