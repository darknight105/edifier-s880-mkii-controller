using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;

namespace S880Ctl;

internal interface IBleDeviceResolver<TDevice> where TDevice : class
{
    Task<BleResolutionResult<TDevice>> ResolveAsync(
        ulong address,
        BluetoothAddressKind addressType,
        CancellationToken cancellationToken);
}

internal interface IBleResolutionTransport<TDevice> where TDevice : class
{
    Task<TDevice?> ResolveAsync(
        ulong address,
        BluetoothAddressKind addressType,
        CancellationToken cancellationToken);

    Task<BleAdapterReadiness> GetAdapterReadinessAsync(CancellationToken cancellationToken);

    IBleAdvertisementSession CreateAdvertisementSession();
}

internal interface IBleAdvertisementSession : IDisposable
{
    event Action<BleAdvertisement>? AdvertisementReceived;
    event Action<BleDiscoveryStopped>? Stopped;

    void Start();
    void Stop();
}

internal sealed class BleDeviceResolver<TDevice> : IBleDeviceResolver<TDevice> where TDevice : class
{
    internal static readonly TimeSpan DefaultDiscoveryBudget = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CleanupBudget = TimeSpan.FromSeconds(3);

    private readonly IBleResolutionTransport<TDevice> _transport;
    private readonly TimeSpan _discoveryBudget;
    private int _attemptInProgress;

    internal BleDeviceResolver(
        IBleResolutionTransport<TDevice> transport,
        TimeSpan? discoveryBudget = null)
    {
        _transport = transport;
        _discoveryBudget = discoveryBudget ?? DefaultDiscoveryBudget;
        if (_discoveryBudget <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(discoveryBudget));
    }

    public async Task<BleResolutionResult<TDevice>> ResolveAsync(
        ulong address,
        BluetoothAddressKind addressType,
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _attemptInProgress, 1, 0) != 0)
        {
            return Failure(
                address, addressType, "fast-address-resolution", "busy", "resolver-busy",
                "A Bluetooth LE connection resolution attempt is already running.",
                recoveryAttempted: false, initialResolution: "not-attempted",
                adapterState: null, discoveryStarted: false, targetObserved: false,
                resolutionAttempts: 0);
        }

        try
        {
            TDevice? device;
            if (cancellationToken.IsCancellationRequested)
                return Cancelled(address, addressType, "fast-address-resolution", false, "not-started", null, false, false, 0);
            try
            {
                device = await _transport.ResolveAsync(address, addressType, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Cancelled(address, addressType, "fast-address-resolution", false, "not-completed", null, false, false, 1);
            }
            catch (Exception error)
            {
                return Failure(
                    address, addressType, "fast-address-resolution", "failed", ErrorCode(error, "resolution-failed"),
                    $"Windows Bluetooth LE address resolution failed: {error.Message}",
                    recoveryAttempted: false, initialResolution: "failed",
                    adapterState: null, discoveryStarted: false, targetObserved: false,
                    resolutionAttempts: 1);
            }

            if (device is not null)
            {
                return Success(address, addressType, device, recoveryAttempted: false, "resolved", null, false, false, 1);
            }

            BleAdapterReadiness readiness;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                readiness = await _transport.GetAdapterReadinessAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Cancelled(address, addressType, "adapter-readiness", true, "not-found", null, false, false, 1);
            }
            catch (Exception error)
            {
                return Failure(
                    address, addressType, "adapter-readiness", "failed", ErrorCode(error, "resolution-failed"),
                    $"Bluetooth adapter readiness check failed: {error.Message}",
                    recoveryAttempted: true, initialResolution: "not-found",
                    adapterState: null, discoveryStarted: false, targetObserved: false,
                    resolutionAttempts: 1);
            }

            if (!readiness.Ready)
            {
                return Failure(
                    address, addressType, "adapter-readiness", readiness.ReadyState, readiness.ErrorCode ?? "resolution-failed",
                    readiness.Message ?? "The Bluetooth adapter is not ready for Bluetooth Low Energy discovery.",
                    recoveryAttempted: true, initialResolution: "not-found",
                    adapterState: readiness.RadioState, discoveryStarted: false, targetObserved: false,
                    resolutionAttempts: 1);
            }

            var discovery = await DiscoverTargetAsync(address, addressType, cancellationToken);
            if (!discovery.TargetObserved)
            {
                if (discovery.ErrorCode == "request-cancelled")
                {
                    return Cancelled(
                        address, addressType, "target-discovery", true, "not-found", readiness.RadioState,
                        discovery.Started, false, 1, discovery.CleanupError);
                }

                return Failure(
                    address, addressType, "target-discovery",
                    discovery.ErrorCode == "target-advertisement-timeout" ? "target-not-observed" : "failed",
                    discovery.ErrorCode ?? "discovery-failed",
                    discovery.Message ?? "Bluetooth LE target discovery failed.",
                    recoveryAttempted: true, initialResolution: "not-found",
                    adapterState: readiness.RadioState, discoveryStarted: discovery.Started, targetObserved: false,
                    resolutionAttempts: 1, cleanupError: discovery.CleanupError);
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                device = await _transport.ResolveAsync(address, addressType, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Cancelled(
                    address, addressType, "post-discovery-resolution", true, "not-found", readiness.RadioState,
                    discovery.Started, true, 2, discovery.CleanupError);
            }
            catch (Exception error)
            {
                return Failure(
                    address, addressType, "post-discovery-resolution", "failed", ErrorCode(error, "resolution-failed"),
                    $"Bluetooth LE address resolution after the target advertisement failed: {error.Message}",
                    recoveryAttempted: true, initialResolution: "not-found",
                    adapterState: readiness.RadioState, discoveryStarted: discovery.Started, targetObserved: true,
                    resolutionAttempts: 2, cleanupError: discovery.CleanupError);
            }

            return device is null
                ? Failure(
                    address, addressType, "post-discovery-resolution", "target-observed-unresolved",
                    "target-advertised-unresolved",
                    "The exact Bluetooth LE target advertised, but Windows still did not return the device.",
                    recoveryAttempted: true, initialResolution: "not-found",
                    adapterState: readiness.RadioState, discoveryStarted: discovery.Started, targetObserved: true,
                    resolutionAttempts: 2, cleanupError: discovery.CleanupError)
                : Success(
                    address, addressType, device, recoveryAttempted: true, "not-found", readiness.RadioState,
                    discovery.Started, true, 2, discovery.CleanupError);
        }
        finally
        {
            Volatile.Write(ref _attemptInProgress, 0);
        }
    }

    private async Task<BleDiscoveryResult> DiscoverTargetAsync(
        ulong address,
        BluetoothAddressKind addressType,
        CancellationToken cancellationToken)
    {
        IBleAdvertisementSession? session = null;
        Action<BleAdvertisement>? received = null;
        Action<BleDiscoveryStopped>? stopped = null;
        var completion = new TaskCompletionSource<BleDiscoveryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stoppedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = false;
        string? cleanupError = null;
        BleDiscoveryResult result;

        try
        {
            session = _transport.CreateAdvertisementSession();
            received = advertisement =>
            {
                if (advertisement.Address == address && advertisement.AddressType == addressType)
                {
                    completion.TrySetResult(new(true, true, null, null, null));
                }
            };
            stopped = result =>
            {
                stoppedSignal.TrySetResult();
                completion.TrySetResult(new(
                    started,
                    false,
                    "discovery-failed",
                    result.Message ?? "Bluetooth LE discovery stopped before the exact target advertised.",
                    null));
            };
            session.AdvertisementReceived += received;
            session.Stopped += stopped;
            session.Start();
            started = true;

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(_discoveryBudget);
            try
            {
                result = await completion.Task.WaitAsync(budget.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                result = new(started, false, "request-cancelled", "Bluetooth LE target discovery was cancelled.", null);
            }
            catch (OperationCanceledException)
            {
                result = new(
                    started,
                    false,
                    "target-advertisement-timeout",
                    $"The exact Bluetooth LE target did not advertise within {_discoveryBudget.TotalSeconds:0.#} seconds. Software discovery cannot make a non-advertising speaker appear; make sure the speaker is powered on and available to its Bluetooth control radio.",
                    null);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = new(started, false, "request-cancelled", "Bluetooth LE target discovery was cancelled.", null);
        }
        catch (Exception error)
        {
            result = new(started, false, ErrorCode(error, "discovery-failed"), $"Bluetooth LE target discovery failed: {error.Message}", null);
        }
        finally
        {
            if (session is not null)
            {
                try
                {
                    if (started)
                    {
                        session.Stop();
                        var cleanupDelay = Task.Delay(CleanupBudget);
                        if (await Task.WhenAny(stoppedSignal.Task, cleanupDelay) != stoppedSignal.Task)
                            cleanupError = "Bluetooth LE discovery did not report Stopped within the 3-second cleanup bound.";
                    }
                }
                catch (Exception error)
                {
                    cleanupError = $"Bluetooth LE discovery cleanup failed: {error.Message}";
                }
                finally
                {
                    try
                    {
                        if (received is not null) session.AdvertisementReceived -= received;
                        if (stopped is not null) session.Stopped -= stopped;
                        session.Dispose();
                    }
                    catch (Exception error)
                    {
                        cleanupError ??= $"Bluetooth LE discovery detach cleanup failed: {error.Message}";
                    }
                }
            }
        }

        return cleanupError is null ? result : result with { CleanupError = cleanupError };
    }

    private BleResolutionResult<TDevice> Success(
        ulong address,
        BluetoothAddressKind addressType,
        TDevice device,
        bool recoveryAttempted,
        string initialResolution,
        string? adapterState,
        bool discoveryStarted,
        bool targetObserved,
        int resolutionAttempts,
        string? cleanupError = null) =>
        new(device, new(
            BluetoothAddress.Format(address), AddressTypeName(addressType), "ready", "ready", null, null,
            recoveryAttempted, initialResolution, adapterState, discoveryStarted, targetObserved,
            (int)_discoveryBudget.TotalSeconds, resolutionAttempts, true, cleanupError));

    private BleResolutionResult<TDevice> Failure(
        ulong address,
        BluetoothAddressKind addressType,
        string stage,
        string readyState,
        string errorCode,
        string message,
        bool recoveryAttempted,
        string initialResolution,
        string? adapterState,
        bool discoveryStarted,
        bool targetObserved,
        int resolutionAttempts,
        string? cleanupError = null) =>
        new(null, new(
            BluetoothAddress.Format(address), AddressTypeName(addressType), stage, readyState, errorCode, message,
            recoveryAttempted, initialResolution, adapterState, discoveryStarted, targetObserved,
            (int)_discoveryBudget.TotalSeconds, resolutionAttempts, false, cleanupError));

    private BleResolutionResult<TDevice> Cancelled(
        ulong address,
        BluetoothAddressKind addressType,
        string stage,
        bool recoveryAttempted,
        string initialResolution,
        string? adapterState,
        bool discoveryStarted,
        bool targetObserved,
        int resolutionAttempts,
        string? cleanupError = null) =>
        Failure(
            address, addressType, stage, "cancelled", "request-cancelled",
            "Bluetooth LE connection resolution was cancelled.", recoveryAttempted, initialResolution,
            adapterState, discoveryStarted, targetObserved, resolutionAttempts, cleanupError);

    private static string AddressTypeName(BluetoothAddressKind addressType) =>
        addressType == BluetoothAddressKind.Public ? "public" : "random";

    private static string ErrorCode(Exception error, string fallback) =>
        error is UnauthorizedAccessException || error.HResult == unchecked((int)0x80070005)
            ? "adapter-access-denied"
            : fallback;
}

internal sealed class NativeBleResolutionTransport : IBleResolutionTransport<BluetoothLEDevice>
{
    public Task<BluetoothLEDevice?> ResolveAsync(
        ulong address,
        BluetoothAddressKind addressType,
        CancellationToken cancellationToken) =>
        WinRtAwait.AwaitAsync(
            BluetoothLEDevice.FromBluetoothAddressAsync(
                address,
                addressType == BluetoothAddressKind.Public ? BluetoothAddressType.Public : BluetoothAddressType.Random),
            cancellationToken,
            static lateDevice => lateDevice?.Dispose());

    public async Task<BleAdapterReadiness> GetAdapterReadinessAsync(CancellationToken cancellationToken)
    {
        var adapter = await WinRtAwait.AwaitAsync(BluetoothAdapter.GetDefaultAsync(), cancellationToken);
        if (adapter is null)
            return BleAdapterReadiness.Unavailable();

        var radio = await WinRtAwait.AwaitAsync(adapter.GetRadioAsync(), cancellationToken);
        if (radio is null)
            return BleAdapterReadiness.MissingRadio();
        var radioState = radio.State.ToString().ToLowerInvariant();
        if (radio.State != Windows.Devices.Radios.RadioState.On)
            return BleAdapterReadiness.Off(radioState);
        if (!adapter.IsLowEnergySupported)
            return BleAdapterReadiness.LowEnergyUnsupported(radioState);
        if (!adapter.IsCentralRoleSupported)
            return BleAdapterReadiness.CentralRoleUnsupported(radioState);
        return BleAdapterReadiness.Available(radioState);
    }

    public IBleAdvertisementSession CreateAdvertisementSession() => new NativeBleAdvertisementSession();
}

internal sealed class NativeBleAdvertisementSession : IBleAdvertisementSession
{
    private readonly BluetoothLEAdvertisementWatcher _watcher = new()
    {
        ScanningMode = BluetoothLEScanningMode.Active,
        AllowExtendedAdvertisements = true
    };

    internal NativeBleAdvertisementSession()
    {
        _watcher.Received += OnReceived;
        _watcher.Stopped += OnStopped;
    }

    public event Action<BleAdvertisement>? AdvertisementReceived;
    public event Action<BleDiscoveryStopped>? Stopped;

    public void Start() => _watcher.Start();

    public void Stop()
    {
        if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
            _watcher.Stop();
    }

    public void Dispose()
    {
        _watcher.Received -= OnReceived;
        _watcher.Stopped -= OnStopped;
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher _, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var addressType = args.BluetoothAddressType switch
        {
            BluetoothAddressType.Public => BluetoothAddressKind.Public,
            BluetoothAddressType.Random => BluetoothAddressKind.Random,
            _ => (BluetoothAddressKind?)null
        };
        if (addressType.HasValue)
            AdvertisementReceived?.Invoke(new(args.BluetoothAddress, addressType.Value));
    }

    private void OnStopped(BluetoothLEAdvertisementWatcher _, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        var message = args.Error == BluetoothError.Success
            ? null
            : $"Bluetooth LE discovery stopped with Bluetooth error: {args.Error}";
        Stopped?.Invoke(new(args.Error.ToString(), message));
    }
}

internal sealed record BleResolutionResult<TDevice>(TDevice? Device, BleConnectionReport Report) where TDevice : class;

internal sealed record BleConnectionReport(
    string TargetAddress,
    string TargetAddressType,
    string Stage,
    string ReadyState,
    string? ErrorCode,
    string? Message,
    bool RecoveryAttempted,
    string InitialResolution,
    string? AdapterState,
    bool DiscoveryStarted,
    bool TargetAdvertisementObserved,
    int MaximumDiscoverySeconds,
    int ResolutionAttempts,
    bool Resolved,
    string? CleanupError);

internal sealed record BleAdapterReadiness(
    bool Ready,
    string ReadyState,
    string? ErrorCode,
    string? Message,
    string? RadioState)
{
    internal static BleAdapterReadiness Available(string radioState) => new(true, "ready", null, null, radioState);
    internal static BleAdapterReadiness Unavailable() => new(false, "adapter-unavailable", "adapter-unavailable", "Windows did not return a Bluetooth adapter.", null);
    internal static BleAdapterReadiness MissingRadio() => new(false, "adapter-unavailable", "adapter-unavailable", "The Windows Bluetooth adapter did not expose a radio.", null);
    internal static BleAdapterReadiness Off(string radioState) => new(false, "adapter-off", "adapter-off", "The Bluetooth radio is not on.", radioState);
    internal static BleAdapterReadiness LowEnergyUnsupported(string radioState) => new(false, "unsupported", "low-energy-unsupported", "The Bluetooth adapter does not support Bluetooth Low Energy.", radioState);
    internal static BleAdapterReadiness CentralRoleUnsupported(string radioState) => new(false, "unsupported", "central-role-unsupported", "The Bluetooth adapter does not support the Bluetooth Low Energy central role.", radioState);
}

internal sealed record BleAdvertisement(ulong Address, BluetoothAddressKind AddressType);
internal sealed record BleDiscoveryStopped(string? ErrorCode, string? Message);
internal sealed record BleDiscoveryResult(bool Started, bool TargetObserved, string? ErrorCode, string? Message, string? CleanupError);
