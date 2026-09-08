using System.Runtime.InteropServices.WindowsRuntime;
using System.Collections.Concurrent;
using System.Text.Json;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Radios;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace S880Ctl;

internal sealed class NativeBluetoothCommands : IBluetoothCommands
{
    private const int MaximumAdvertisementRecords = 50_000;
    private readonly IBleDeviceResolver<BluetoothLEDevice> _deviceResolver;

    public NativeBluetoothCommands()
        : this(new BleDeviceResolver<BluetoothLEDevice>(new NativeBleResolutionTransport()))
    {
    }

    internal NativeBluetoothCommands(IBleDeviceResolver<BluetoothLEDevice> deviceResolver)
    {
        _deviceResolver = deviceResolver;
    }

    public async Task<int> AdapterAsync(ICommandConsole console, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            var radios = await WinRtAwait.AwaitAsync(Radio.GetRadiosAsync(), timeout.Token);
            var adapter = await WinRtAwait.AwaitAsync(BluetoothAdapter.GetDefaultAsync(), timeout.Token);
            var bluetoothRadios = radios.Where(radio => radio.Kind == RadioKind.Bluetooth)
                .Select(radio => new { radio.Name, state = radio.State.ToString().ToLowerInvariant() })
                .ToArray();

            var available = adapter is not null;
            console.WriteLine(JsonSerializer.Serialize(new
            {
                command = "adapter",
                status = available ? "completed" : "unavailable",
                exitCode = available ? ExitCodes.Success : ExitCodes.AdapterUnavailable,
                adapter = adapter is null ? null : new
                {
                    adapter.BluetoothAddress,
                    address = BluetoothAddress.Format(adapter.BluetoothAddress),
                    adapter.IsLowEnergySupported,
                    adapter.IsCentralRoleSupported,
                    adapter.IsPeripheralRoleSupported,
                    adapter.IsAdvertisementOffloadSupported
                },
                radios = bluetoothRadios,
                changedRadioState = false
            }, JsonDefaults.Options));
            return available ? ExitCodes.Success : ExitCodes.AdapterUnavailable;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            console.WriteError(S880Application.StatusJson("adapter", "timeout", ExitCodes.Timeout, "adapter query exceeded 15 seconds"));
            return ExitCodes.Timeout;
        }
        catch (Exception error)
        {
            console.WriteError(S880Application.StatusJson("adapter", "error", ExitCodes.BluetoothError, error.Message));
            return ExitCodes.BluetoothError;
        }
    }

    public async Task<int> DiscoverAsync(DiscoverCommand command, ICommandConsole console, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var records = new List<AdvertisementRecord>();
        var errors = new List<string>();
        var droppedRecords = 0;
        var sync = new object();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = command.Active ? BluetoothLEScanningMode.Active : BluetoothLEScanningMode.Passive,
            AllowExtendedAdvertisements = true
        };

        TypedEventHandler<BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementReceivedEventArgs>? receivedHandler = null;
        TypedEventHandler<BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementWatcherStoppedEventArgs>? stoppedHandler = null;
        receivedHandler = (_, args) =>
        {
            try
            {
                var record = ToAdvertisementRecord(args);
                lock (sync)
                {
                    if (records.Count < MaximumAdvertisementRecords) records.Add(record);
                    else droppedRecords++;
                }
            }
            catch (Exception error)
            {
                lock (sync) errors.Add($"advertisement decode failed: {error.Message}");
            }
        };
        stoppedHandler = (_, args) =>
        {
            if (args.Error != BluetoothError.Success)
            {
                lock (sync) errors.Add($"watcher stopped with Bluetooth error: {args.Error}");
            }
            stopped.TrySetResult();
        };

        watcher.Received += receivedHandler;
        watcher.Stopped += stoppedHandler;
        var cancelled = false;
        var finalWatcherStatus = watcher.Status;
        try
        {
            watcher.Start();
            await Task.Delay(TimeSpan.FromSeconds(command.Seconds), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (Exception error)
        {
            lock (sync) errors.Add($"watcher failed: {error.Message}");
        }
        finally
        {
            try
            {
                if (watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
                {
                    watcher.Stop();
                }

                if (watcher.Status == BluetoothLEAdvertisementWatcherStatus.Stopping)
                {
                    await Task.WhenAny(stopped.Task, Task.Delay(TimeSpan.FromSeconds(3)));
                }

                finalWatcherStatus = watcher.Status;
                if (finalWatcherStatus == BluetoothLEAdvertisementWatcherStatus.Stopping)
                {
                    lock (sync) errors.Add("watcher did not reach Stopped within the 3-second cleanup bound");
                }
            }
            catch (Exception error)
            {
                lock (sync) errors.Add($"watcher cleanup failed: {error.Message}");
            }
            finally
            {
                watcher.Received -= receivedHandler;
                watcher.Stopped -= stoppedHandler;
            }
        }

        AdvertisementRecord[] snapshot;
        string[] errorSnapshot;
        lock (sync)
        {
            snapshot = records.ToArray();
            errorSnapshot = errors.Distinct(StringComparer.Ordinal).ToArray();
        }

        var exitCode = cancelled ? ExitCodes.Cancelled : errorSnapshot.Length > 0 ? ExitCodes.BluetoothError : ExitCodes.Success;
        var report = new
        {
            command = "discover",
            status = cancelled ? "cancelled" : errorSnapshot.Length > 0 ? "completed-with-errors" : "completed",
            exitCode,
            scanMode = command.Active ? "active" : "passive",
            extendedAdvertisementsAllowed = true,
            requestedSeconds = command.Seconds,
            startedAtUtc = startedAt,
            completedAtUtc = DateTimeOffset.UtcNow,
            recordCount = snapshot.Length,
            droppedRecords,
            watcherFinalStatus = finalWatcherStatus.ToString(),
            identityPolicy = "S880 name matches and Edifier manufacturer-company matches are candidates only; no model identity is verified by discovery",
            records = snapshot,
            errors = errorSnapshot
        };

        if (!await TryWriteReportAsync(command.OutputPath, report, console)) return ExitCodes.OutputError;
        var output = Path.GetFullPath(command.OutputPath);
        var statusJson = S880Application.StatusJson("discover", report.status, exitCode, errorSnapshot.FirstOrDefault(), output);
        if (exitCode == ExitCodes.Success) console.WriteLine(statusJson); else console.WriteError(statusJson);
        return exitCode;
    }

    public async Task<int> GattAsync(GattCommand command, ICommandConsole console, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var errors = new List<string>();
        var services = new List<GattServiceRecord>();
        string? deviceName = null;
        string? deviceId = null;
        var status = "error";
        var exitCode = ExitCodes.BluetoothError;
        BleConnectionReport? connection = null;
        string? gattFailureKind = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(command.TimeoutSeconds));

        BluetoothLEDevice? device = null;
        try
        {
            var resolution = await _deviceResolver.ResolveAsync(command.Address, command.AddressType, timeout.Token);
            connection = resolution.Report;
            device = resolution.Device;

            if (device is null)
            {
                timeout.Token.ThrowIfCancellationRequested();
                errors.Add(connection.Message ?? "Windows did not return a Bluetooth LE device for the exact address");
            }
            else
            {
                deviceName = device.Name;
                deviceId = device.DeviceId;
                var serviceResult = await WinRtAwait.AwaitAsync(
                    device.GetGattServicesAsync(BluetoothCacheMode.Uncached),
                    timeout.Token,
                    DisposeLateServices);

                try
                {
                    if (serviceResult.Status != GattCommunicationStatus.Success)
                    {
                        errors.Add(CommunicationError("service discovery", serviceResult.Status, serviceResult.ProtocolError));
                        gattFailureKind = GattFailureKind(serviceResult.Status);
                    }
                    else
                    {
                        foreach (var service in serviceResult.Services)
                        {
                            var characteristicResult = await WinRtAwait.AwaitAsync(
                                service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached),
                                timeout.Token);
                            var characteristicRecords = characteristicResult.Status == GattCommunicationStatus.Success
                                ? characteristicResult.Characteristics.Select(ToCharacteristicRecord).ToArray()
                                : [];

                            if (characteristicResult.Status != GattCommunicationStatus.Success)
                            {
                                if (characteristicResult.Status == GattCommunicationStatus.AccessDenied || gattFailureKind is null)
                                    gattFailureKind = GattFailureKind(characteristicResult.Status);
                                errors.Add(CommunicationError(
                                    $"characteristic discovery for {service.Uuid}",
                                    characteristicResult.Status,
                                    characteristicResult.ProtocolError));
                            }

                            services.Add(new(
                                service.Uuid.ToString("D"),
                                service.AttributeHandle,
                                characteristicResult.Status.ToString(),
                                characteristicResult.ProtocolError,
                                characteristicRecords));
                        }
                    }
                }
                finally
                {
                    DisposeLateServices(serviceResult);
                }

                status = errors.Count == 0 ? "completed" : "completed-with-errors";
                exitCode = errors.Count == 0 ? ExitCodes.Success : ExitCodes.BluetoothError;
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                status = "cancelled";
                exitCode = ExitCodes.Cancelled;
                errors.Add("cancelled; outstanding Windows connection work was cancelled on a best-effort basis");
            }
            else
            {
                status = "timeout";
                exitCode = ExitCodes.Timeout;
                if (connection?.Resolved == true) gattFailureKind = "gatt-communication-timeout";
                else connection = ConnectionTimedOut(connection);
                errors.Add($"GATT enumeration exceeded {command.TimeoutSeconds} seconds; Windows connection cancellation is best-effort");
            }
        }
        catch (Exception error)
        {
            errors.Add(error.Message);
            if (connection?.Resolved == true) gattFailureKind = "gatt-communication-failed";
        }
        finally
        {
            device?.Dispose();
        }

        if (errors.Count > 0 && connection?.Resolved == true)
            gattFailureKind ??= "gatt-communication-failed";
        var report = new
        {
            command = "gatt",
            status,
            exitCode,
            errorCategory = ConnectionErrorCategory(status, connection?.ErrorCode ?? gattFailureKind),
            failureKind = ConnectionFailureKind(status, connection?.ErrorCode ?? gattFailureKind),
            retryable = IsRetryableConnectionFailure(status, connection?.ErrorCode ?? gattFailureKind),
            stage = ConnectionStage(connection, gattFailureKind),
            startedAtUtc = startedAt,
            completedAtUtc = DateTimeOffset.UtcNow,
            target = new { address = command.AddressText, addressType = command.AddressType.ToString().ToLowerInvariant() },
            deviceName,
            deviceId,
            connection,
            cacheMode = "uncached",
            safety = new
            {
                exactAddressOnly = true,
                characteristicValuesRead = false,
                characteristicValuesWritten = false,
                cccdSubscriptionsCreated = false,
                maintainConnectionRequested = false,
                disconnectNote = "References are disposed; Windows controls the eventual physical disconnect and may finish an already-started connection operation."
            },
            services,
            errors
        };

        if (!await TryWriteReportAsync(command.OutputPath, report, console)) return ExitCodes.OutputError;
        var output = Path.GetFullPath(command.OutputPath);
        var statusJson = S880Application.StatusJson("gatt", status, exitCode, errors.FirstOrDefault(), output);
        if (exitCode == ExitCodes.Success) console.WriteLine(statusJson); else console.WriteError(statusJson);
        return exitCode;
    }

    public async Task<int> SourceAsync(SourceCommand command, ICommandConsole console, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var errors = new ConcurrentQueue<string>();
        var cleanupErrors = new ConcurrentQueue<string>();
        SourceProtocolOutcome? outcome = null;
        string? deviceName = null;
        string? deviceId = null;
        ushort? serviceHandle = null;
        ushort? notifyHandle = null;
        ushort? transmitHandle = null;
        var cccdAttempted = false;
        var cccdEnabled = false;
        var status = "error";
        var exitCode = ExitCodes.BluetoothError;
        BleConnectionReport? connection = null;
        string? gattFailureKind = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(command.TimeoutSeconds));

        BluetoothLEDevice? device = null;
        GattDeviceServicesResult? serviceResult = null;
        GattCharacteristic? notify = null;
        NotificationInbox? sourceInbox = null;
        GattEcExchange? exchange = null;
        TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs>? valueChanged = null;
        try
        {
            var resolution = await _deviceResolver.ResolveAsync(command.Address, BluetoothAddressKind.Public, timeout.Token);
            connection = resolution.Report;
            device = resolution.Device;
            if (device is null)
            {
                timeout.Token.ThrowIfCancellationRequested();
                throw new GattExchangeException(connection.Message ?? "Windows did not return the evidence-bound Bluetooth LE device");
            }
            deviceName = device.Name;
            deviceId = device.DeviceId;

            serviceResult = await WinRtAwait.AwaitAsync(
                device.GetGattServicesForUuidAsync(EdifierS880Mk2CnProtocol.ServiceUuid, BluetoothCacheMode.Uncached),
                timeout.Token,
                DisposeLateServices);
            if (serviceResult.Status != GattCommunicationStatus.Success)
                throw new GattExchangeException(
                    CommunicationError("source service discovery", serviceResult.Status, serviceResult.ProtocolError),
                    GattFailureKind(serviceResult.Status));
            if (serviceResult.Services.Count != 1)
                throw new SourceProtocolException($"expected exactly one source service; found {serviceResult.Services.Count}");

            var service = serviceResult.Services[0];
            serviceHandle = service.AttributeHandle;
            var notifyResult = await WinRtAwait.AwaitAsync(
                service.GetCharacteristicsForUuidAsync(EdifierS880Mk2CnProtocol.NotifyUuid, BluetoothCacheMode.Uncached),
                timeout.Token);
            var transmitResult = await WinRtAwait.AwaitAsync(
                service.GetCharacteristicsForUuidAsync(EdifierS880Mk2CnProtocol.TransmitUuid, BluetoothCacheMode.Uncached),
                timeout.Token);
            if (notifyResult.Status != GattCommunicationStatus.Success)
                throw new GattExchangeException(
                    CommunicationError("notify characteristic discovery", notifyResult.Status, notifyResult.ProtocolError),
                    GattFailureKind(notifyResult.Status));
            if (transmitResult.Status != GattCommunicationStatus.Success)
                throw new GattExchangeException(
                    CommunicationError("transmit characteristic discovery", transmitResult.Status, transmitResult.ProtocolError),
                    GattFailureKind(transmitResult.Status));
            if (notifyResult.Characteristics.Count != 1 || transmitResult.Characteristics.Count != 1)
                throw new SourceProtocolException("expected exactly one notify and one transmit characteristic");

            notify = notifyResult.Characteristics[0];
            var transmit = transmitResult.Characteristics[0];
            notifyHandle = notify.AttributeHandle;
            transmitHandle = transmit.AttributeHandle;
            var requiredNotify = GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify;
            var requiredTransmit = GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse;
            if ((notify.CharacteristicProperties & requiredNotify) != requiredNotify)
                throw new SourceProtocolException($"notify characteristic properties did not match captured layout: {notify.CharacteristicProperties}");
            if ((transmit.CharacteristicProperties & requiredTransmit) != requiredTransmit)
                throw new SourceProtocolException($"transmit characteristic properties did not match captured layout: {transmit.CharacteristicProperties}");

            sourceInbox = new NotificationInbox();
            valueChanged = (_, args) =>
            {
                try { sourceInbox.Push(ReadBytes(args.CharacteristicValue)); }
                catch (Exception error) { errors.Enqueue($"notification decode failed: {error.Message}"); }
            };
            notify.ValueChanged += valueChanged;
            timeout.Token.ThrowIfCancellationRequested();
            cccdAttempted = true;
            var subscriptionStatus = await WinRtAwait.AwaitAsync(
                notify.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify),
                timeout.Token);
            if (subscriptionStatus != GattCommunicationStatus.Success)
                throw new GattExchangeException(
                    $"notification subscription failed: {subscriptionStatus}",
                    GattFailureKind(subscriptionStatus));
            cccdEnabled = true;

            exchange = new GattEcExchange(transmit, sourceInbox);
            outcome = await EcSourceController.ExecuteAsync(command.Operation, exchange, timeout.Token);
            status = "completed";
            exitCode = ExitCodes.Success;
        }
        catch (SourceProtocolException error)
        {
            errors.Enqueue(error.Message);
            status = exchange?.SetAttempted == true ? "outcome-uncertain" : "protocol-rejected";
            exitCode = ExitCodes.ProtocolNotVerified;
        }
        catch (GattExchangeException error)
        {
            errors.Enqueue(error.Message);
            gattFailureKind = connection?.ErrorCode ?? error.FailureKind;
            if (exchange?.SetAttempted == true) status = "outcome-uncertain";
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                status = exchange?.SetAttempted == true ? "outcome-uncertain" : "cancelled";
                exitCode = ExitCodes.Cancelled;
                errors.Enqueue("cancelled; outstanding Windows connection work was cancelled on a best-effort basis");
            }
            else
            {
                status = exchange?.SetAttempted == true ? "outcome-uncertain" : "timeout";
                exitCode = ExitCodes.Timeout;
                if (connection?.Resolved == true) gattFailureKind = "gatt-communication-timeout";
                else connection = ConnectionTimedOut(connection);
                errors.Enqueue($"source operation exceeded {command.TimeoutSeconds} seconds; Windows connection cancellation is best-effort");
            }
        }
        catch (Exception error)
        {
            errors.Enqueue(error.Message);
            if (exchange?.SetAttempted == true) status = "outcome-uncertain";
        }
        finally
        {
            if (notify is not null && cccdAttempted)
            {
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    var cleanupStatus = await WinRtAwait.AwaitAsync(
                        notify.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None),
                        cleanupTimeout.Token);
                    if (cleanupStatus != GattCommunicationStatus.Success)
                        cleanupErrors.Enqueue($"notification unsubscribe failed: {cleanupStatus}");
                }
                catch (Exception error)
                {
                    cleanupErrors.Enqueue($"notification unsubscribe cleanup failed: {error.Message}");
                }
            }

            if (notify is not null && valueChanged is not null) notify.ValueChanged -= valueChanged;
            if (serviceResult is not null) DisposeLateServices(serviceResult);
            device?.Dispose();
        }

        if (exitCode == ExitCodes.Success && !cleanupErrors.IsEmpty)
        {
            status = "completed-with-cleanup-errors";
            exitCode = ExitCodes.BluetoothError;
        }

        var report = new
        {
            command = "source",
            operation = command.Operation.ToString().ToLowerInvariant(),
            status,
            exitCode,
            errorCategory = ConnectionErrorCategory(status, connection?.ErrorCode ?? gattFailureKind),
            failureKind = ConnectionFailureKind(status, connection?.ErrorCode ?? gattFailureKind),
            retryable = IsRetryableConnectionFailure(status, connection?.ErrorCode ?? gattFailureKind),
            stage = ConnectionStage(connection, gattFailureKind),
            startedAtUtc = startedAt,
            completedAtUtc = DateTimeOffset.UtcNow,
            target = new { address = command.AddressText, addressType = "public", evidenceBound = true },
            deviceName,
            deviceId,
            connection,
            identity = outcome is null ? null : new { outcome.ProductName, outcome.IdentityFrameHex, exactMatch = true },
            gatt = new
            {
                serviceUuid = EdifierS880Mk2CnProtocol.ServiceUuid,
                serviceHandle,
                notifyUuid = EdifierS880Mk2CnProtocol.NotifyUuid,
                notifyHandle,
                transmitUuid = EdifierS880Mk2CnProtocol.TransmitUuid,
                transmitHandle,
                cacheMode = "uncached",
                writeOption = "write-without-response",
                characteristicValuesRead = false
            },
            result = outcome is null ? null : new
            {
                currentBefore = new { name = outcome.CurrentBefore, code = $"0x{outcome.CurrentBeforeCode:X2}" },
                requestedCode = outcome.RequestedCode.HasValue ? $"0x{outcome.RequestedCode.Value:X2}" : null,
                outcome.SetFrameHex,
                outcome.AckFrameHex,
                currentAfter = new { name = outcome.CurrentAfter, code = $"0x{outcome.CurrentAfterCode:X2}" },
                outcome.Verification,
                outcome.WritesHex
            },
            parser = new
            {
                framing = "BB EC command lengthHi lengthLo payload checksum8",
                maximumPayloadBytes = 512,
                diagnostics = sourceInbox?.Diagnostics
            },
            safety = new
            {
                identityQueryRequired = true,
                initialSourceQueryRequired = true,
                automaticRetries = false,
                d8Sent = false,
                rawWriteAvailable = false,
                setAttempted = exchange?.SetAttempted ?? false,
                attemptedWritesHex = exchange?.WritesHex ?? [],
                cccdEnableAttempted = cccdAttempted,
                cccdEnableConfirmed = cccdEnabled,
                cccdDisableAttempted = cccdAttempted,
                cccdDisabledDuringCleanup = cccdAttempted && cleanupErrors.IsEmpty
            },
            errors = errors.ToArray(),
            cleanupErrors = cleanupErrors.ToArray()
        };
        if (!await TryWriteReportAsync(command.OutputPath, report, console)) return ExitCodes.OutputError;
        var statusJson = S880Application.StatusJson("source", status, exitCode, errors.FirstOrDefault(), Path.GetFullPath(command.OutputPath));
        if (exitCode == ExitCodes.Success) console.WriteLine(statusJson); else console.WriteError(statusJson);
        return exitCode;
    }

    public async Task<int> EqAsync(EqCommand command, ICommandConsole console, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var errors = new ConcurrentQueue<string>();
        var cleanupErrors = new ConcurrentQueue<string>();
        EqProtocolOutcome? outcome = null;
        CustomEqGainOutcome? customGainOutcome = null;
        CustomEqGainTrace? customGainTrace = null;
        string? deviceName = null;
        string? deviceId = null;
        ushort? serviceHandle = null;
        ushort? notifyHandle = null;
        ushort? transmitHandle = null;
        var cccdAttempted = false;
        var cccdEnabled = false;
        var status = "error";
        var exitCode = ExitCodes.BluetoothError;
        BleConnectionReport? connection = null;
        string? gattFailureKind = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(command.TimeoutSeconds));

        BluetoothLEDevice? device = null;
        GattDeviceServicesResult? serviceResult = null;
        GattCharacteristic? notify = null;
        NotificationInbox? eqInbox = null;
        GattEcExchange? exchange = null;
        TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs>? valueChanged = null;
        try
        {
            var resolution = await _deviceResolver.ResolveAsync(command.Address, BluetoothAddressKind.Public, timeout.Token);
            connection = resolution.Report;
            device = resolution.Device;
            if (device is null)
            {
                timeout.Token.ThrowIfCancellationRequested();
                throw new GattExchangeException(connection.Message ?? "Windows did not return the evidence-bound Bluetooth LE device");
            }
            deviceName = device.Name;
            deviceId = device.DeviceId;

            serviceResult = await WinRtAwait.AwaitAsync(
                device.GetGattServicesForUuidAsync(EdifierS880Mk2CnProtocol.ServiceUuid, BluetoothCacheMode.Uncached),
                timeout.Token,
                DisposeLateServices);
            if (serviceResult.Status != GattCommunicationStatus.Success)
                throw new GattExchangeException(
                    CommunicationError("EQ service discovery", serviceResult.Status, serviceResult.ProtocolError),
                    GattFailureKind(serviceResult.Status));
            if (serviceResult.Services.Count != 1)
                throw new EqProtocolException($"expected exactly one EQ service; found {serviceResult.Services.Count}");

            var service = serviceResult.Services[0];
            serviceHandle = service.AttributeHandle;
            var notifyResult = await WinRtAwait.AwaitAsync(
                service.GetCharacteristicsForUuidAsync(EdifierS880Mk2CnProtocol.NotifyUuid, BluetoothCacheMode.Uncached),
                timeout.Token);
            var transmitResult = await WinRtAwait.AwaitAsync(
                service.GetCharacteristicsForUuidAsync(EdifierS880Mk2CnProtocol.TransmitUuid, BluetoothCacheMode.Uncached),
                timeout.Token);
            if (notifyResult.Status != GattCommunicationStatus.Success)
                throw new GattExchangeException(
                    CommunicationError("EQ notify characteristic discovery", notifyResult.Status, notifyResult.ProtocolError),
                    GattFailureKind(notifyResult.Status));
            if (transmitResult.Status != GattCommunicationStatus.Success)
                throw new GattExchangeException(
                    CommunicationError("EQ transmit characteristic discovery", transmitResult.Status, transmitResult.ProtocolError),
                    GattFailureKind(transmitResult.Status));
            if (notifyResult.Characteristics.Count != 1 || transmitResult.Characteristics.Count != 1)
                throw new EqProtocolException("expected exactly one notify and one transmit characteristic");

            notify = notifyResult.Characteristics[0];
            var transmit = transmitResult.Characteristics[0];
            notifyHandle = notify.AttributeHandle;
            transmitHandle = transmit.AttributeHandle;
            var requiredNotify = GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify;
            var requiredTransmit = GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse;
            if ((notify.CharacteristicProperties & requiredNotify) != requiredNotify)
                throw new EqProtocolException($"notify characteristic properties did not match captured layout: {notify.CharacteristicProperties}");
            if ((transmit.CharacteristicProperties & requiredTransmit) != requiredTransmit)
                throw new EqProtocolException($"transmit characteristic properties did not match captured layout: {transmit.CharacteristicProperties}");

            eqInbox = new NotificationInbox();
            valueChanged = (_, args) =>
            {
                try { eqInbox.Push(ReadBytes(args.CharacteristicValue)); }
                catch (Exception error) { errors.Enqueue($"notification decode failed: {error.Message}"); }
            };
            notify.ValueChanged += valueChanged;
            timeout.Token.ThrowIfCancellationRequested();
            cccdAttempted = true;
            var subscriptionStatus = await WinRtAwait.AwaitAsync(
                notify.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify),
                timeout.Token);
            if (subscriptionStatus != GattCommunicationStatus.Success)
                throw new GattExchangeException(
                    $"notification subscription failed: {subscriptionStatus}",
                    GattFailureKind(subscriptionStatus));
            cccdEnabled = true;

            exchange = new GattEcExchange(transmit, eqInbox);
            if (command.Operation == EqOperation.CustomSet)
            {
                if (command.Band is null || command.GainDb is null)
                    throw new EqProtocolException("Custom EQ gain command was missing band or gain");
                customGainTrace = new CustomEqGainTrace();
                customGainOutcome = await EcCustomEqGainController.ExecuteAsync(
                    command.Band.Value, command.GainDb.Value, exchange, timeout.Token, customGainTrace);
            }
            else
            {
                outcome = await EcEqController.ExecuteAsync(command.Operation, exchange, timeout.Token, command.Preset);
            }
            status = "completed";
            exitCode = ExitCodes.Success;
        }
        catch (EqProtocolException error)
        {
            errors.Enqueue(error.Message);
            status = exchange?.SetAttempted == true ? "outcome-uncertain" : "protocol-rejected";
            exitCode = ExitCodes.ProtocolNotVerified;
        }
        catch (GattExchangeException error)
        {
            errors.Enqueue(error.Message);
            gattFailureKind = connection?.ErrorCode ?? error.FailureKind;
            if (exchange?.SetAttempted == true) status = "outcome-uncertain";
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                status = exchange?.SetAttempted == true ? "outcome-uncertain" : "cancelled";
                exitCode = ExitCodes.Cancelled;
                errors.Enqueue("cancelled; outstanding Windows connection work was cancelled on a best-effort basis");
            }
            else
            {
                status = exchange?.SetAttempted == true ? "outcome-uncertain" : "timeout";
                exitCode = ExitCodes.Timeout;
                if (connection?.Resolved == true) gattFailureKind = "gatt-communication-timeout";
                else connection = ConnectionTimedOut(connection);
                errors.Enqueue($"EQ query exceeded {command.TimeoutSeconds} seconds; Windows connection cancellation is best-effort");
            }
        }
        catch (Exception error)
        {
            errors.Enqueue(error.Message);
            if (exchange?.SetAttempted == true) status = "outcome-uncertain";
        }
        finally
        {
            if (notify is not null && cccdAttempted)
            {
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    var cleanupStatus = await WinRtAwait.AwaitAsync(
                        notify.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None),
                        cleanupTimeout.Token);
                    if (cleanupStatus != GattCommunicationStatus.Success)
                        cleanupErrors.Enqueue($"notification unsubscribe failed: {cleanupStatus}");
                }
                catch (Exception error)
                {
                    cleanupErrors.Enqueue($"notification unsubscribe cleanup failed: {error.Message}");
                }
            }

            if (notify is not null && valueChanged is not null) notify.ValueChanged -= valueChanged;
            if (serviceResult is not null) DisposeLateServices(serviceResult);
            device?.Dispose();
        }

        if (exitCode == ExitCodes.Success && !cleanupErrors.IsEmpty)
        {
            status = "completed-with-cleanup-errors";
            exitCode = ExitCodes.BluetoothError;
        }

        var operationName = EdifierS880Mk2CnEqProtocol.OperationName(command.Operation);
        var writesHex = exchange?.WritesHex ?? [];
        var presetModeChangeSent = writesHex.Any(frame => frame.StartsWith("AAECC4", StringComparison.Ordinal));
        var customGainWriteSent = writesHex.Any(frame => frame.StartsWith("AAEC44", StringComparison.Ordinal));
        object? identityReport = null;
        if (outcome is not null)
            identityReport = new { outcome.ProductName, outcome.IdentityFrameHex, exactMatch = true };
        else if (customGainOutcome is not null)
            identityReport = new { customGainOutcome.ProductName, customGainOutcome.IdentityFrameHex, exactMatch = true };
        else if (customGainTrace?.ProductName is not null)
            identityReport = new { ProductName = customGainTrace.ProductName, customGainTrace.IdentityFrameHex, exactMatch = true };
        object? resultReport = customGainOutcome is not null
            ? new
            {
                queryReceived = true,
                customGainOutcome.Band,
                customGainOutcome.FrequencyHz,
                requestedGainRaw = customGainOutcome.RequestedGainRaw,
                requestedGainDb = customGainOutcome.RequestedGainDb,
                presetBefore = new { name = customGainOutcome.PresetBeforeName, code = $"0x{customGainOutcome.PresetBeforeCode:X2}" },
                customGainOutcome.PresetBeforeFrameHex,
                before = ToCustomEqSnapshotReport(customGainOutcome.Before, customGainOutcome.CustomBeforeFrameHex),
                customGainOutcome.SetFrameHex,
                customGainOutcome.AckFrameHex,
                ackPayloadByte = customGainOutcome.AckPayloadByte.HasValue ? $"0x{customGainOutcome.AckPayloadByte.Value:X2}" : null,
                customGainOutcome.AckInterpretation,
                after = ToCustomEqSnapshotReport(customGainOutcome.After, customGainOutcome.CustomAfterFrameHex),
                presetAfter = new { name = customGainOutcome.PresetAfterName, code = $"0x{customGainOutcome.PresetAfterCode:X2}" },
                customGainOutcome.PresetAfterFrameHex,
                customGainOutcome.FirmwareChangedPreset,
                customGainOutcome.Verification,
                customGainOutcome.WritesHex
            }
            : outcome is not null
                ? new
                {
                    queryReceived = true,
                    outcome.QueryFrameHex,
                    outcome.ResponseFrameHex,
                    outcome.RawPayloadHex,
                    presetCode = outcome.PresetCode.HasValue ? $"0x{outcome.PresetCode.Value:X2}" : null,
                    outcome.PresetName,
                    customFormatByte = outcome.CustomFormatByte.HasValue ? $"0x{outcome.CustomFormatByte.Value:X2}" : null,
                    customEq = outcome.CustomSnapshot is null ? null : ToCustomEqSnapshotReport(outcome.CustomSnapshot, outcome.ResponseFrameHex),
                    outcome.SemanticDecoded,
                    outcome.Interpretation,
                    currentBefore = outcome.CurrentBeforeCode.HasValue
                        ? new { name = outcome.CurrentBeforeName, code = $"0x{outcome.CurrentBeforeCode.Value:X2}" }
                        : null,
                    requestedPreset = outcome.RequestedPresetCode.HasValue
                        ? new { name = outcome.RequestedPresetName, code = $"0x{outcome.RequestedPresetCode.Value:X2}" }
                        : null,
                    outcome.SetFrameHex,
                    outcome.AckFrameHex,
                    ackPayloadByte = outcome.AckPayloadByte.HasValue ? $"0x{outcome.AckPayloadByte.Value:X2}" : null,
                    outcome.AckInterpretation,
                    outcome.CurrentBeforeFrameHex,
                    outcome.CurrentAfterFrameHex,
                    currentAfter = outcome.CurrentAfterCode.HasValue
                        ? new { name = outcome.CurrentAfterName, code = $"0x{outcome.CurrentAfterCode.Value:X2}" }
                        : null,
                    outcome.Verification,
                    outcome.WritesHex
                }
                : customGainTrace is not null
                    ? ToCustomEqTraceReport(customGainTrace)
                : null;
        var report = new
        {
            command = "eq",
            operation = operationName,
            requestedPreset = command.Preset?.ToString().ToLowerInvariant(),
            requestedBand = command.Band,
            requestedGainDb = command.GainDb,
            status,
            exitCode,
            errorCategory = ConnectionErrorCategory(status, connection?.ErrorCode ?? gattFailureKind),
            failureKind = ConnectionFailureKind(status, connection?.ErrorCode ?? gattFailureKind),
            retryable = IsRetryableConnectionFailure(status, connection?.ErrorCode ?? gattFailureKind),
            stage = ConnectionStage(connection, gattFailureKind),
            startedAtUtc = startedAt,
            completedAtUtc = DateTimeOffset.UtcNow,
            target = new { address = command.AddressText, addressType = "public", evidenceBound = true },
            deviceName,
            deviceId,
            connection,
            identity = identityReport,
            gatt = new
            {
                serviceUuid = EdifierS880Mk2CnProtocol.ServiceUuid,
                serviceHandle,
                notifyUuid = EdifierS880Mk2CnProtocol.NotifyUuid,
                notifyHandle,
                transmitUuid = EdifierS880Mk2CnProtocol.TransmitUuid,
                transmitHandle,
                cacheMode = "uncached",
                writeOption = "write-without-response",
                characteristicValuesRead = false
            },
            result = resultReport,
            parser = new
            {
                framing = "BB EC command lengthHi lengthLo payload checksum8",
                maximumPayloadBytes = 512,
                diagnostics = eqInbox?.Diagnostics
            },
            safety = new
            {
                identityQueryRequired = true,
                selectedQuery = command.Operation switch
                {
                    EqOperation.Get => "D5",
                    EqOperation.CustomGet => "43",
                    EqOperation.Set => "D5,C4,D5",
                    EqOperation.CustomSet => "D5,43,44,43,D5",
                    _ => "none"
                },
                allowedCommands = command.Operation switch
                {
                    EqOperation.Get => new[] { "C9", "D5" },
                    EqOperation.CustomGet => new[] { "C9", "43" },
                    EqOperation.Set => new[] { "C9", "D5", "C4" },
                    EqOperation.CustomSet => new[] { "C9", "D5", "43", "44" },
                    _ => []
                },
                automaticRetries = false,
                stateChangingCommandSent = exchange?.SetAttempted ?? false,
                presetModeChangeSent,
                customGainWriteSent,
                d8Sent = false,
                rawWriteAvailable = false,
                attemptedWritesHex = exchange?.WritesHex ?? [],
                cccdEnableAttempted = cccdAttempted,
                cccdEnableConfirmed = cccdEnabled,
                cccdDisableAttempted = cccdAttempted,
                cccdDisabledDuringCleanup = cccdAttempted && cleanupErrors.IsEmpty
            },
            errors = errors.ToArray(),
            cleanupErrors = cleanupErrors.ToArray()
        };
        if (!await TryWriteReportAsync(command.OutputPath, report, console)) return ExitCodes.OutputError;
        var statusJson = S880Application.StatusJson("eq", status, exitCode, errors.FirstOrDefault(), Path.GetFullPath(command.OutputPath));
        if (exitCode == ExitCodes.Success) console.WriteLine(statusJson); else console.WriteError(statusJson);
        return exitCode;
    }

    public async Task<int> VolumeAsync(VolumeCommand command, ICommandConsole console, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var errors = new ConcurrentQueue<string>();
        var cleanupErrors = new ConcurrentQueue<string>();
        VolumeProtocolOutcome? outcome = null;
        string? deviceName = null;
        string? deviceId = null;
        ushort? serviceHandle = null;
        ushort? notifyHandle = null;
        ushort? transmitHandle = null;
        var cccdAttempted = false;
        var cccdEnabled = false;
        var status = "error";
        var exitCode = ExitCodes.BluetoothError;
        BleConnectionReport? connection = null;
        string? gattFailureKind = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(command.TimeoutSeconds));

        BluetoothLEDevice? device = null;
        GattDeviceServicesResult? serviceResult = null;
        GattCharacteristic? notify = null;
        NotificationInbox? volumeInbox = null;
        GattEcExchange? exchange = null;
        TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs>? valueChanged = null;
        try
        {
            var resolution = await _deviceResolver.ResolveAsync(command.Address, BluetoothAddressKind.Public, timeout.Token);
            connection = resolution.Report;
            device = resolution.Device;
            if (device is null)
            {
                timeout.Token.ThrowIfCancellationRequested();
                throw new GattExchangeException(connection.Message ?? "Windows did not return the evidence-bound Bluetooth LE device");
            }
            deviceName = device.Name;
            deviceId = device.DeviceId;

            serviceResult = await WinRtAwait.AwaitAsync(
                device.GetGattServicesForUuidAsync(EdifierS880Mk2CnProtocol.ServiceUuid, BluetoothCacheMode.Uncached),
                timeout.Token,
                DisposeLateServices);
            if (serviceResult.Status != GattCommunicationStatus.Success)
                throw new GattExchangeException(
                    CommunicationError("volume service discovery", serviceResult.Status, serviceResult.ProtocolError),
                    GattFailureKind(serviceResult.Status));
            if (serviceResult.Services.Count != 1)
                throw new VolumeProtocolException($"expected exactly one volume service; found {serviceResult.Services.Count}");

            var service = serviceResult.Services[0];
            serviceHandle = service.AttributeHandle;
            var notifyResult = await WinRtAwait.AwaitAsync(
                service.GetCharacteristicsForUuidAsync(EdifierS880Mk2CnProtocol.NotifyUuid, BluetoothCacheMode.Uncached),
                timeout.Token);
            var transmitResult = await WinRtAwait.AwaitAsync(
                service.GetCharacteristicsForUuidAsync(EdifierS880Mk2CnProtocol.TransmitUuid, BluetoothCacheMode.Uncached),
                timeout.Token);
            if (notifyResult.Status != GattCommunicationStatus.Success)
                throw new GattExchangeException(
                    CommunicationError("volume notify characteristic discovery", notifyResult.Status, notifyResult.ProtocolError),
                    GattFailureKind(notifyResult.Status));
            if (transmitResult.Status != GattCommunicationStatus.Success)
                throw new GattExchangeException(
                    CommunicationError("volume transmit characteristic discovery", transmitResult.Status, transmitResult.ProtocolError),
                    GattFailureKind(transmitResult.Status));
            if (notifyResult.Characteristics.Count != 1 || transmitResult.Characteristics.Count != 1)
                throw new VolumeProtocolException("expected exactly one notify and one transmit characteristic");

            notify = notifyResult.Characteristics[0];
            var transmit = transmitResult.Characteristics[0];
            notifyHandle = notify.AttributeHandle;
            transmitHandle = transmit.AttributeHandle;
            var requiredNotify = GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify;
            var requiredTransmit = GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse;
            if ((notify.CharacteristicProperties & requiredNotify) != requiredNotify)
                throw new VolumeProtocolException($"notify characteristic properties did not match captured layout: {notify.CharacteristicProperties}");
            if ((transmit.CharacteristicProperties & requiredTransmit) != requiredTransmit)
                throw new VolumeProtocolException($"transmit characteristic properties did not match captured layout: {transmit.CharacteristicProperties}");

            volumeInbox = new NotificationInbox();
            valueChanged = (_, args) =>
            {
                try { volumeInbox.Push(ReadBytes(args.CharacteristicValue)); }
                catch (Exception error) { errors.Enqueue($"notification decode failed: {error.Message}"); }
            };
            notify.ValueChanged += valueChanged;
            timeout.Token.ThrowIfCancellationRequested();
            cccdAttempted = true;
            var subscriptionStatus = await WinRtAwait.AwaitAsync(
                notify.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify),
                timeout.Token);
            if (subscriptionStatus != GattCommunicationStatus.Success)
                throw new GattExchangeException(
                    $"notification subscription failed: {subscriptionStatus}",
                    GattFailureKind(subscriptionStatus));
            cccdEnabled = true;

            exchange = new GattEcExchange(transmit, volumeInbox);
            outcome = await EcVolumeController.ExecuteAsync(command.Operation, command.Value, exchange, timeout.Token);
            status = "completed";
            exitCode = ExitCodes.Success;
        }
        catch (VolumeProtocolException error)
        {
            errors.Enqueue(error.Message);
            status = exchange?.SetAttempted == true ? "outcome-uncertain" : "protocol-rejected";
            exitCode = ExitCodes.ProtocolNotVerified;
        }
        catch (EqProtocolException error)
        {
            errors.Enqueue(error.Message);
            status = "protocol-rejected";
            exitCode = ExitCodes.ProtocolNotVerified;
        }
        catch (GattExchangeException error)
        {
            errors.Enqueue(error.Message);
            gattFailureKind = connection?.ErrorCode ?? error.FailureKind;
            if (exchange?.SetAttempted == true) status = "outcome-uncertain";
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                status = exchange?.SetAttempted == true ? "outcome-uncertain" : "cancelled";
                exitCode = ExitCodes.Cancelled;
                errors.Enqueue("cancelled; outstanding Windows connection work was cancelled on a best-effort basis");
            }
            else
            {
                status = exchange?.SetAttempted == true ? "outcome-uncertain" : "timeout";
                exitCode = ExitCodes.Timeout;
                if (connection?.Resolved == true) gattFailureKind = "gatt-communication-timeout";
                else connection = ConnectionTimedOut(connection);
                errors.Enqueue($"volume operation exceeded {command.TimeoutSeconds} seconds; Windows connection cancellation is best-effort");
            }
        }
        catch (Exception error)
        {
            errors.Enqueue(error.Message);
            if (exchange?.SetAttempted == true) status = "outcome-uncertain";
        }
        finally
        {
            if (notify is not null && cccdAttempted)
            {
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    var cleanupStatus = await WinRtAwait.AwaitAsync(
                        notify.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None),
                        cleanupTimeout.Token);
                    if (cleanupStatus != GattCommunicationStatus.Success)
                        cleanupErrors.Enqueue($"notification unsubscribe failed: {cleanupStatus}");
                }
                catch (Exception error)
                {
                    cleanupErrors.Enqueue($"notification unsubscribe cleanup failed: {error.Message}");
                }
            }

            if (notify is not null && valueChanged is not null) notify.ValueChanged -= valueChanged;
            if (serviceResult is not null) DisposeLateServices(serviceResult);
            device?.Dispose();
        }

        if (exitCode == ExitCodes.Success && !cleanupErrors.IsEmpty)
        {
            status = "completed-with-cleanup-errors";
            exitCode = ExitCodes.BluetoothError;
        }

        var report = new
        {
            command = "volume",
            operation = command.Operation.ToString().ToLowerInvariant(),
            requestedValue = command.Value,
            status,
            exitCode,
            errorCategory = ConnectionErrorCategory(status, connection?.ErrorCode ?? gattFailureKind),
            failureKind = ConnectionFailureKind(status, connection?.ErrorCode ?? gattFailureKind),
            retryable = IsRetryableConnectionFailure(status, connection?.ErrorCode ?? gattFailureKind),
            stage = ConnectionStage(connection, gattFailureKind),
            startedAtUtc = startedAt,
            completedAtUtc = DateTimeOffset.UtcNow,
            target = new { address = command.AddressText, addressType = "public", evidenceBound = true },
            deviceName,
            deviceId,
            connection,
            identity = outcome is null ? null : new { outcome.ProductName, outcome.IdentityFrameHex, exactMatch = true },
            gatt = new
            {
                serviceUuid = EdifierS880Mk2CnProtocol.ServiceUuid,
                serviceHandle,
                notifyUuid = EdifierS880Mk2CnProtocol.NotifyUuid,
                notifyHandle,
                transmitUuid = EdifierS880Mk2CnProtocol.TransmitUuid,
                transmitHandle,
                cacheMode = "uncached",
                writeOption = "write-without-response",
                characteristicValuesRead = false
            },
            result = outcome is null ? null : new
            {
                maximum = outcome.Maximum,
                currentBefore = outcome.CurrentBefore,
                outcome.Requested,
                outcome.QueryFrameHex,
                outcome.CurrentBeforeFrameHex,
                outcome.SetFrameHex,
                outcome.AckFrameHex,
                currentAfter = outcome.CurrentAfter,
                outcome.CurrentAfterFrameHex,
                outcome.Verification,
                outcome.WritesHex
            },
            parser = new
            {
                framing = "BB EC command lengthHi lengthLo payload checksum8",
                maximumPayloadBytes = 512,
                diagnostics = volumeInbox?.Diagnostics
            },
            safety = new
            {
                identityQueryRequired = true,
                initialVolumeQueryRequired = true,
                explicitReadbackRequired = command.Operation == VolumeOperation.Set,
                allowedCommands = command.Operation == VolumeOperation.Get ? new[] { "C9", "66" } : new[] { "C9", "66", "67" },
                automaticRetries = false,
                muteCommandAvailable = false,
                setAttempted = exchange?.SetAttempted ?? false,
                attemptedWritesHex = exchange?.WritesHex ?? [],
                cccdEnableAttempted = cccdAttempted,
                cccdEnableConfirmed = cccdEnabled,
                cccdDisableAttempted = cccdAttempted,
                cccdDisabledDuringCleanup = cccdAttempted && cleanupErrors.IsEmpty
            },
            errors = errors.ToArray(),
            cleanupErrors = cleanupErrors.ToArray()
        };
        if (!await TryWriteReportAsync(command.OutputPath, report, console)) return ExitCodes.OutputError;
        var statusJson = S880Application.StatusJson("volume", status, exitCode, errors.FirstOrDefault(), Path.GetFullPath(command.OutputPath));
        if (exitCode == ExitCodes.Success) console.WriteLine(statusJson); else console.WriteError(statusJson);
        return exitCode;
    }

    private static object ToCustomEqSnapshotReport(CustomEqSnapshot snapshot, string frameHex) => new
    {
        frameHex,
        rawPayloadHex = Convert.ToHexString(snapshot.RawPayload),
        format = $"0x{snapshot.Format:X2}",
        count = snapshot.Count,
        bands = snapshot.Bands.Select(band => new
        {
            band.Band,
            band.RecordIndex,
            band.Reserved,
            band.FrequencyHz,
            band.GainRaw,
            band.GainDb,
            band.QRaw
        }).ToArray(),
        opaqueTailHex = Convert.ToHexString(snapshot.OpaqueTail)
    };

    private static object ToCustomEqTraceReport(CustomEqGainTrace trace) => new
    {
        partialEvidence = true,
        trace.Band,
        trace.RequestedGainDb,
        requestedGainRaw = trace.RequestedGainRaw,
        presetBefore = trace.PresetBeforeCode.HasValue
            ? new { name = trace.PresetBeforeName, code = $"0x{trace.PresetBeforeCode.Value:X2}" }
            : null,
        trace.PresetBeforeFrameHex,
        before = trace.Before is not null && trace.CustomBeforeFrameHex is not null
            ? ToCustomEqSnapshotReport(trace.Before, trace.CustomBeforeFrameHex)
            : null,
        trace.SetFrameHex,
        trace.AckFrameHex,
        ackPayloadByte = trace.AckPayloadByte.HasValue ? $"0x{trace.AckPayloadByte.Value:X2}" : null,
        after = trace.After is not null && trace.CustomAfterFrameHex is not null
            ? ToCustomEqSnapshotReport(trace.After, trace.CustomAfterFrameHex)
            : null,
        presetAfter = trace.PresetAfterCode.HasValue
            ? new { name = trace.PresetAfterName, code = $"0x{trace.PresetAfterCode.Value:X2}" }
            : null,
        trace.PresetAfterFrameHex
    };

    private static AdvertisementRecord ToAdvertisementRecord(BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var name = args.Advertisement.LocalName;
        var candidate = AssessCandidate(name, args.Advertisement.ManufacturerData.Select(data => data.CompanyId));
        return new(
            args.Timestamp,
            BluetoothAddress.Format(args.BluetoothAddress),
            args.BluetoothAddressType.ToString().ToLowerInvariant(),
            name,
            args.RawSignalStrengthInDBm,
            args.AdvertisementType.ToString(),
            args.TransmitPowerLevelInDBm,
            args.Advertisement.ServiceUuids.Select(uuid => uuid.ToString("D")).ToArray(),
            args.Advertisement.ManufacturerData.Select(data => new ManufacturerRecord(data.CompanyId, ReadHex(data.Data))).ToArray(),
            args.Advertisement.DataSections.Select(section => new DataSectionRecord(section.DataType, ReadHex(section.Data))).ToArray(),
            candidate);
    }

    internal static CandidateAssessment AssessCandidate(string? name, IEnumerable<ushort> manufacturerCompanyIds)
    {
        var nameMatch = !string.IsNullOrWhiteSpace(name) && name.Contains("S880", StringComparison.OrdinalIgnoreCase);
        var edifierManufacturerMatch = manufacturerCompanyIds.Contains((ushort)0x07E0);
        var candidateReason = (nameMatch, edifierManufacturerMatch) switch
        {
            (true, true) => "advertised name contains S880 and manufacturer company ID 0x07E0 identifies Edifier; candidate only",
            (true, false) => "advertised name contains S880; candidate only",
            (false, true) => "manufacturer company ID 0x07E0 identifies Edifier; vendor candidate only, model unverified",
            _ => null
        };
        return new(nameMatch, edifierManufacturerMatch, false, candidateReason);
    }

    private static GattCharacteristicRecord ToCharacteristicRecord(GattCharacteristic characteristic)
    {
        var properties = characteristic.CharacteristicProperties;
        return new(
            characteristic.Uuid.ToString("D"),
            characteristic.AttributeHandle,
            properties.ToString(),
            properties.HasFlag(GattCharacteristicProperties.Read),
            properties.HasFlag(GattCharacteristicProperties.Write),
            properties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse),
            properties.HasFlag(GattCharacteristicProperties.Notify),
            properties.HasFlag(GattCharacteristicProperties.Indicate));
    }

    private static string ReadHex(IBuffer buffer)
    {
        return Convert.ToHexString(ReadBytes(buffer));
    }

    private static byte[] ReadBytes(IBuffer buffer)
    {
        using var reader = DataReader.FromBuffer(buffer);
        var bytes = new byte[reader.UnconsumedBufferLength];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static string CommunicationError(string operation, GattCommunicationStatus status, byte? protocolError) =>
        protocolError.HasValue
            ? $"{operation} failed: {status}, protocol error 0x{protocolError.Value:X2}"
            : $"{operation} failed: {status}";

    internal static string GattFailureKind(GattCommunicationStatus status) =>
        status == GattCommunicationStatus.AccessDenied ? "gatt-access-denied" : "gatt-communication-failed";

    internal static string? ConnectionErrorCategory(string status, string? failureKind)
    {
        if (string.Equals(status, "outcome-uncertain", StringComparison.Ordinal) || failureKind is null)
            return null;
        return failureKind switch
        {
            "request-cancelled" => "cancelled",
            "resolver-busy" => "busy",
            "low-energy-unsupported" or "central-role-unsupported" => "unsupported",
            "adapter-access-denied" or "gatt-access-denied" => "permission",
            _ => "connection"
        };
    }

    internal static string? ConnectionFailureKind(string status, string? failureKind) =>
        string.Equals(status, "outcome-uncertain", StringComparison.Ordinal) ? null : failureKind;

    internal static bool IsRetryableConnectionFailure(string status, string? failureKind)
    {
        if (string.Equals(status, "outcome-uncertain", StringComparison.Ordinal)) return false;
        return failureKind is
            "adapter-unavailable" or
            "adapter-off" or
            "target-advertisement-timeout" or
            "target-advertised-unresolved" or
            "discovery-failed" or
            "resolution-failed" or
            "resolution-timeout" or
            "gatt-communication-failed" or
            "gatt-communication-timeout";
    }

    internal static BleConnectionReport? ConnectionTimedOut(BleConnectionReport? connection)
    {
        if (connection is null || connection.Resolved || connection.ErrorCode != "request-cancelled")
            return connection;
        return connection with
        {
            ReadyState = "failed",
            ErrorCode = "resolution-timeout",
            Message = "Bluetooth LE connection resolution exceeded the command time budget."
        };
    }

    internal static string? ConnectionStage(BleConnectionReport? connection, string? gattFailureKind) =>
        connection?.ErrorCode is not null ? connection.Stage : gattFailureKind is not null ? "gatt" : connection?.Stage;

    private static void DisposeLateServices(GattDeviceServicesResult result)
    {
        foreach (var service in result.Services) service.Dispose();
    }

    private static async Task<bool> TryWriteReportAsync(string path, object report, ICommandConsole console)
    {
        string? temporaryPath = null;
        try
        {
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
            var json = JsonSerializer.Serialize(report, JsonDefaults.Options);
            await File.WriteAllTextAsync(temporaryPath, json);
            File.Move(temporaryPath, fullPath, true);
            return true;
        }
        catch (Exception error)
        {
            console.WriteError(S880Application.StatusJson("output", "error", ExitCodes.OutputError, error.Message, path));
            return false;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); } catch { }
            }
        }
    }
}

internal sealed class GattEcExchange(GattCharacteristic transmit, NotificationInbox inbox) : IEcExchange
{
    private readonly List<string> _writesHex = [];
    public IReadOnlyList<string> WritesHex => _writesHex;
    public bool SetAttempted { get; private set; }
    public EcFrameWaiter RegisterWaiter(Func<EcFrame, bool> predicate) => inbox.Register(predicate);

    public async Task WriteAsync(byte[] frame, CancellationToken cancellationToken)
    {
        using var writer = new DataWriter();
        writer.WriteBytes(frame);
        var buffer = writer.DetachBuffer();
        cancellationToken.ThrowIfCancellationRequested();
        _writesHex.Add(Convert.ToHexString(frame));
        if (frame.Length > 2 && frame[2] is 0x62 or 0xC4 or 0x44 or 0x67) SetAttempted = true;
        var result = await WinRtAwait.AwaitAsync(
            transmit.WriteValueWithResultAsync(buffer, GattWriteOption.WriteWithoutResponse),
            cancellationToken);
        if (result.Status != GattCommunicationStatus.Success)
        {
            var detail = result.ProtocolError.HasValue ? $", protocol error 0x{result.ProtocolError.Value:X2}" : string.Empty;
            throw new GattExchangeException(
                $"GATT write failed: {result.Status}{detail}",
                NativeBluetoothCommands.GattFailureKind(result.Status));
        }
    }
}

internal sealed class GattExchangeException(
    string message,
    string failureKind = "gatt-communication-failed") : Exception(message)
{
    internal string FailureKind { get; } = failureKind;
}

internal static class WinRtAwait
{
    public static async Task<T> AwaitAsync<T>(IAsyncOperation<T> operation, CancellationToken cancellationToken, Action<T>? disposeLateResult = null)
    {
        var operationTask = operation.AsTask();
        if (operationTask.IsCompleted) return await operationTask;

        var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        if (await Task.WhenAny(operationTask, cancellationTask) == operationTask)
        {
            return await operationTask;
        }

        try { operation.Cancel(); } catch { }
        _ = operationTask.ContinueWith(
            completed =>
            {
                if (completed.Status == TaskStatus.RanToCompletion) disposeLateResult?.Invoke(completed.Result);
                _ = completed.Exception;
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        throw new OperationCanceledException(cancellationToken);
    }
}

internal sealed record AdvertisementRecord(
    DateTimeOffset TimestampUtc,
    string Address,
    string AddressType,
    string Name,
    short RssiDbm,
    string AdvertisementType,
    short? TransmitPowerDbm,
    string[] ServiceUuids,
    ManufacturerRecord[] ManufacturerData,
    DataSectionRecord[] DataSections,
    CandidateAssessment Candidate);
internal sealed record ManufacturerRecord(ushort CompanyId, string DataHex);
internal sealed record DataSectionRecord(byte DataType, string DataHex);
internal sealed record CandidateAssessment(bool NameMatch, bool EdifierManufacturerMatch, bool IdentityVerified, string? Reason);
internal sealed record GattServiceRecord(string Uuid, ushort AttributeHandle, string Status, byte? ProtocolError, GattCharacteristicRecord[] Characteristics);
internal sealed record GattCharacteristicRecord(
    string Uuid,
    ushort AttributeHandle,
    string Properties,
    bool Read,
    bool Write,
    bool WriteWithoutResponse,
    bool Notify,
    bool Indicate);
