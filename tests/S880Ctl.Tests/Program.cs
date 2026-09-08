using System.Text.Json;
using System.Text;
using S880Ctl;

var tests = new (string Name, Func<Task> Run)[]
{
    ("discover defaults to passive for 20 seconds", DiscoverDefaults),
    ("discover accepts explicit active scan", DiscoverActive),
    ("discover requires an output path", DiscoverRequiresOutput),
    ("gatt parses exact address and address type", GattParsesTarget),
    ("gatt rejects malformed address", GattRejectsMalformedAddress),
    ("gatt requires explicit address type", GattRequiresAddressType),
    ("address formatting is stable", AddressFormatting),
    ("source target options parse with a 60-second maximum", SourceTargetParses),
    ("all six source names parse and map to official APK values", AllSourcesParseAndMap),
    ("EQ Get actions parse with explicit target options", EqTargetsParse),
    ("compiled speaker binding is either normalized or safely unconfigured", CompiledBindingIsWellFormed),
    ("unknown EQ actions fail before constructing Bluetooth", UnknownEqActionFailsBeforeBluetooth),
    ("unbound EQ target fails before constructing Bluetooth", UnboundEqFailsBeforeBluetooth),
    ("unbound source target fails before constructing Bluetooth", UnboundSourceFailsBeforeBluetooth),
    ("configured binding accepts only its exact public target", ConfiguredBindingEnforcesExactPublicTarget),
    ("raw write command is unavailable", RawWriteIsUnavailable),
    ("adapter dispatches through Bluetooth only after parsing", AdapterDispatches),
    ("Edifier manufacturer ID is vendor evidence only", EdifierManufacturerIsCandidateOnly),
    ("BLE resolution recovers only after the exact target advertisement", BleResolutionRecoversAfterExactTarget),
    ("BLE resolution ignores wrong addresses and address types", BleResolutionIgnoresWrongTargets),
    ("BLE resolution reports bounded target discovery timeout and cleans up", BleResolutionTimeoutCleansUp),
    ("BLE resolution stops before discovery when the adapter is off", BleResolutionAdapterOff),
    ("BLE resolution cancellation stops and unsubscribes discovery", BleResolutionCancellationCleansUp),
    ("BLE resolution rejects a concurrent attempt", BleResolutionRejectsConcurrentAttempt),
    ("BLE connection report retryability excludes unsafe and permanent failures", BleConnectionRetryabilityIsStable),
    ("captured outbound EC vectors are exact", CapturedOutboundVectors),
    ("EQ preset query ignores unrelated notifications and decodes a known single byte", EqPresetQueryDecodesKnownValue),
    ("Custom EQ query preserves raw payload and reports only its first format byte", CustomEqQueryPreservesRawPayload),
    ("validated format-03 Custom EQ query decodes six read-only bands", CustomEqQueryDecodesValidatedFormat03),
    ("Custom EQ gain conversion enforces the exact half-decibel range", CustomEqGainConversionBoundaries),
    ("Custom EQ band set changes one gain byte and verifies the complete payload", CustomEqBandSetRoundTrip),
    ("already-current Custom EQ gain performs no command 44 write", CustomEqGainAlreadyCurrent),
    ("unsupported Custom EQ format blocks before command 44", UnsupportedCustomEqFormatBlocksWrite),
    ("mismatched Custom EQ band index blocks before command 44", MismatchedCustomEqIndexBlocksWrite),
    ("unexpected Custom EQ metadata change leaves one uncertain write", UnexpectedCustomEqMetadataChangeFails),
    ("cancellation after command 44 preserves preflight rollback evidence", CustomEqCancellationPreservesPreflight),
    ("unexpected long EQ preset payload remains raw and undecoded", UnexpectedPresetPayloadRemainsRaw),
    ("wrong identity blocks before the selected EQ query", WrongIdentityBlocksEqQuery),
    ("bad EQ response times out without a retry or extra query", BadEqResponseTimesOutWithoutRetry),
    ("EQ preset set requires known preflight, ACK, and explicit matching readback", EqPresetSetRoundTrip),
    ("already-current EQ preset performs no state-changing write", EqPresetAlreadyCurrent),
    ("unknown initial EQ payload blocks before preset set", UnknownInitialEqBlocksSet),
    ("C4 reply 00 with matching D5 readback verifies the target preset", AckZeroWithMatchingReadbackPasses),
    ("C4 reply 00 with old D5 readback remains an uncertain single attempt", AckZeroWithOldReadbackFails),
    ("malformed C4 reply stops without an extra query or retry", MalformedEqAckDoesNotContinue),
    ("C4 reply 01 alone is insufficient when D5 readback is missing", MissingEqReadbackDoesNotRetry),
    ("cancellation after EQ preflight prevents preset set", EqCancellationBeforeSet),
    ("volume Get and Set parse with exact target and zero-to-thirty bounds", VolumeTargetsParse),
    ("unknown volume action fails before constructing Bluetooth", UnknownVolumeActionFailsBeforeBluetooth),
    ("unbound volume target fails before constructing Bluetooth", UnboundVolumeFailsBeforeBluetooth),
    ("volume query ignores unrelated notifications and decodes max/current", VolumeQueryDecodesMaxAndCurrent),
    ("volume set requires preflight, ACK, and explicit matching readback", VolumeSetRoundTrip),
    ("zero volume uses the same verified readback state machine", VolumeZeroRoundTrip),
    ("already-current volume performs no command 67 write", VolumeAlreadyCurrent),
    ("wrong identity blocks before volume query", WrongIdentityBlocksVolume),
    ("unexpected volume range blocks before command 67", UnexpectedVolumeRangeBlocksSet),
    ("legacy one-byte volume ACK is rejected for this S880 profile", FailedVolumeAckDoesNotRetry),
    ("missing volume readback times out without repeating command 67", MissingVolumeReadbackDoesNotRetry),
    ("cancellation after volume preflight prevents command 67", VolumeCancellationBeforeSet),
    ("EC parser handles fragmented and coalesced frames", ParserHandlesFragmentationAndCoalescing),
    ("EC parser rejects a bad checksum and resynchronizes", ParserRejectsBadChecksum),
    ("notification waiter ignores unrelated valid commands", WaiterIgnoresUnrelatedCommands),
    ("source set requires identity, current status, ACK, and final status", SourceSetSequence),
    ("source set uses one explicit status query when notification is absent", SourceSetExplicitVerification),
    ("unknown current source blocks before command 62", UnknownCurrentSourceBlocksSet),
    ("wrong product identity blocks before source query", WrongIdentityBlocksSource),
    ("cancellation after preflight prevents command 62", CancellationBeforeSetPreventsWrite),
    ("cancellation after command 62 preserves uncertain attempt evidence", CancellationAfterSetPreservesAttempt),
    ("generated profile is sanitized and matches the compiled binding", ProfileIsSanitizedAndMatchesBuild)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception error)
    {
        failures.Add($"FAIL {test.Name}: {error.Message}");
        Console.Error.WriteLine(failures[^1]);
    }
}

Console.WriteLine($"{tests.Length - failures.Count}/{tests.Length} tests passed");
return failures.Count == 0 ? 0 : 1;

static Task DiscoverDefaults()
{
    var result = CliParser.Parse(["discover", "--output", "scan.json"]);
    var command = AssertType<DiscoverCommand>(result.Command);
    Equal(20, command.Seconds);
    False(command.Active);
    Equal("scan.json", command.OutputPath);
    return Task.CompletedTask;
}

static Task DiscoverActive()
{
    var result = CliParser.Parse(["discover", "--active", "--seconds", "7", "--output", "scan.json"]);
    var command = AssertType<DiscoverCommand>(result.Command);
    True(command.Active);
    Equal(7, command.Seconds);
    return Task.CompletedTask;
}

static Task DiscoverRequiresOutput()
{
    var result = CliParser.Parse(["discover"]);
    False(result.IsSuccess);
    Contains("requires --output", result.Error);
    return Task.CompletedTask;
}

static Task GattParsesTarget()
{
    var result = CliParser.Parse([
        "gatt", "--address", "AA:BB:CC:DD:EE:FF", "--address-type", "random", "--output", "gatt.json"]);
    var command = AssertType<GattCommand>(result.Command);
    Equal(0xAABBCCDDEEFFUL, command.Address);
    Equal("AA:BB:CC:DD:EE:FF", command.AddressText);
    Equal(BluetoothAddressKind.Random, command.AddressType);
    return Task.CompletedTask;
}

static Task GattRejectsMalformedAddress()
{
    var result = CliParser.Parse([
        "gatt", "--address", "not-an-address", "--address-type", "public", "--output", "gatt.json"]);
    False(result.IsSuccess);
    Contains("48-bit", result.Error);
    return Task.CompletedTask;
}

static Task GattRequiresAddressType()
{
    var result = CliParser.Parse(["gatt", "--address", "AA:BB:CC:DD:EE:FF", "--output", "gatt.json"]);
    False(result.IsSuccess);
    Contains("address-type", result.Error);
    return Task.CompletedTask;
}

static Task AddressFormatting()
{
    True(BluetoothAddress.TryParse("aa-bb-cc-dd-ee-ff", out var address));
    Equal("AA:BB:CC:DD:EE:FF", BluetoothAddress.Format(address));
    False(BluetoothAddress.TryParse("AA:BB:CC:DD:EE", out _));
    return Task.CompletedTask;
}

static Task SourceTargetParses()
{
    var result = CliParser.Parse([
        "source", "get", "--address", "02:00:00:00:00:01", "--address-type", "public",
        "--output", "source.json", "--timeout-seconds", "60"]);
    var command = AssertType<SourceCommand>(result.Command);
    Equal(SourceOperation.Get, command.Operation);
    Equal(0x020000000001UL, command.Address);
    Equal(60, command.TimeoutSeconds);
    False(CliParser.Parse([
        "source", "get", "--address", "02:00:00:00:00:01", "--address-type", "public",
        "--output", "source.json", "--timeout-seconds", "61"]).IsSuccess);
    return Task.CompletedTask;
}

static Task AllSourcesParseAndMap()
{
    var cases = new[]
    {
        (Name: "usb", Operation: SourceOperation.Usb, Code: (byte)0x01),
        (Name: "line-in-1", Operation: SourceOperation.LineIn1, Code: (byte)0x02),
        (Name: "line-in-2", Operation: SourceOperation.LineIn2, Code: (byte)0x03),
        (Name: "bluetooth", Operation: SourceOperation.Bluetooth, Code: (byte)0x04),
        (Name: "optical", Operation: SourceOperation.Optical, Code: (byte)0x05),
        (Name: "coaxial", Operation: SourceOperation.Coaxial, Code: (byte)0x06)
    };

    foreach (var source in cases)
    {
        var result = CliParser.Parse([
            "source", source.Name, "--address", "02:00:00:00:00:01", "--address-type", "public", "--output", "source.json"]);
        var command = AssertType<SourceCommand>(result.Command);
        Equal(source.Operation, command.Operation);
        Equal(source.Code, EdifierS880Mk2CnProtocol.SourceCode(command.Operation));
        Equal(source.Name, EdifierS880Mk2CnProtocol.SourceName(source.Code));
    }

    return Task.CompletedTask;
}

static Task EqTargetsParse()
{
    foreach (var (action, operation) in new[]
    {
        ("get", EqOperation.Get),
        ("custom-get", EqOperation.CustomGet)
    })
    {
        var result = CliParser.Parse([
            "eq", action, "--address", "02:00:00:00:00:01", "--address-type", "public",
            "--output", "eq.json", "--timeout-seconds", "60"]);
        var command = AssertType<EqCommand>(result.Command);
        Equal(operation, command.Operation);
        Equal(0x020000000001UL, command.Address);
        Equal(BluetoothAddressKind.Public, command.AddressType);
        Equal(60, command.TimeoutSeconds);
        Equal(null, command.Preset);
    }

    var setResult = CliParser.Parse([
        "eq", "set", "monitor", "--address", "02:00:00:00:00:01", "--address-type", "public", "--output", "eq.json"]);
    var setCommand = AssertType<EqCommand>(setResult.Command);
    Equal(EqOperation.Set, setCommand.Operation);
    Equal((EqPreset?)EqPreset.Monitor, setCommand.Preset);
    False(CliParser.Parse([
        "eq", "set", "unknown", "--address", "02:00:00:00:00:01", "--address-type", "public", "--output", "eq.json"]).IsSuccess);

    var customSetResult = CliParser.Parse([
        "eq", "custom-set", "--band", "3", "--gain-db", "+0.5", "--address", "02:00:00:00:00:01",
        "--address-type", "public", "--output", "eq.json"]);
    var customSet = AssertType<EqCommand>(customSetResult.Command);
    Equal(EqOperation.CustomSet, customSet.Operation);
    Equal(3, customSet.Band);
    Equal(0.5m, customSet.GainDb);
    Equal(null, customSet.Preset);
    False(CliParser.Parse([
        "eq", "custom-set", "--band", "3", "--gain-db", "1.25", "--address", "02:00:00:00:00:01",
        "--address-type", "public", "--output", "eq.json"]).IsSuccess);
    False(CliParser.Parse([
        "eq", "custom-set", "--band", "7", "--gain-db", "0", "--address", "02:00:00:00:00:01",
        "--address-type", "public", "--output", "eq.json"]).IsSuccess);

    False(CliParser.Parse([
        "eq", "get", "--address", "02:00:00:00:00:01", "--address-type", "public",
        "--output", "eq.json", "--timeout-seconds", "61"]).IsSuccess);
    return Task.CompletedTask;
}

static async Task CompiledBindingIsWellFormed()
{
    var binding = SpeakerTargetBinding.Current;
    if (!binding.IsConfigured)
    {
        Equal("", binding.AddressText);
        Equal(0UL, binding.Address);
        var bluetoothConstructed = false;
        var blocked = await S880Application.RunAsync(
            ["source", "get", "--address", "02:00:00:00:00:01", "--address-type", "public", "--output", "source.json"],
            new CaptureConsole(),
            () => { bluetoothConstructed = true; throw new InvalidOperationException("Bluetooth must not be constructed"); },
            CancellationToken.None);
        Equal(ExitCodes.ProtocolNotVerified, blocked);
        False(bluetoothConstructed);
        return;
    }

    True(binding.Address != 0);
    Equal(BluetoothAddress.Format(binding.Address), binding.AddressText);
    var fake = new FakeBluetoothCommands();
    var dispatched = await S880Application.RunAsync(
        ["source", "get", "--address", binding.AddressText, "--address-type", "public", "--output", "source.json"],
        new CaptureConsole(), () => fake, CancellationToken.None);
    Equal(71, dispatched);
    True(fake.SourceCalled);
}

static async Task UnknownEqActionFailsBeforeBluetooth()
{
    var bluetoothConstructed = false;
    var exitCode = await S880Application.RunAsync(
        ["eq", "set"],
        new CaptureConsole(),
        () =>
        {
            bluetoothConstructed = true;
            throw new InvalidOperationException("Bluetooth must not be constructed");
        },
        CancellationToken.None);

    Equal(ExitCodes.UsageError, exitCode);
    False(bluetoothConstructed);
}

static async Task UnboundEqFailsBeforeBluetooth()
{
    var bluetoothConstructed = false;
    var console = new CaptureConsole();
    var exitCode = await S880Application.RunAsync(
        ["eq", "get", "--address", "AA:BB:CC:DD:EE:FF", "--address-type", "public", "--output", "eq.json"],
        console,
        () =>
        {
            bluetoothConstructed = true;
            throw new InvalidOperationException("Bluetooth must not be constructed");
        },
        CancellationToken.None,
        SpeakerTargetBinding.Unconfigured);

    Equal(ExitCodes.ProtocolNotVerified, exitCode);
    False(bluetoothConstructed);
    Contains("no configured speaker address", console.ErrorText);
}

static async Task UnboundSourceFailsBeforeBluetooth()
{
    var bluetoothConstructed = false;
    var console = new CaptureConsole();
    var exitCode = await S880Application.RunAsync(
        ["source", "usb", "--address", "AA:BB:CC:DD:EE:FF", "--address-type", "public", "--output", "source.json"],
        console,
        () =>
        {
            bluetoothConstructed = true;
            throw new InvalidOperationException("Bluetooth must not be constructed");
        },
        CancellationToken.None,
        SpeakerTargetBinding.Unconfigured);
    Equal(ExitCodes.ProtocolNotVerified, exitCode);
    False(bluetoothConstructed);
    Contains("no configured speaker address", console.ErrorText);
}

static async Task ConfiguredBindingEnforcesExactPublicTarget()
{
    var binding = SpeakerTargetBinding.Create("02:00:00:00:00:01");
    var fake = new FakeBluetoothCommands();

    Equal(71, await S880Application.RunAsync(
        ["source", "get", "--address", binding.AddressText, "--address-type", "public", "--output", "source.json"],
        new CaptureConsole(), () => fake, CancellationToken.None, binding));
    Equal(72, await S880Application.RunAsync(
        ["eq", "get", "--address", binding.AddressText, "--address-type", "public", "--output", "eq.json"],
        new CaptureConsole(), () => fake, CancellationToken.None, binding));
    Equal(73, await S880Application.RunAsync(
        ["volume", "get", "--address", binding.AddressText, "--address-type", "public", "--output", "volume.json"],
        new CaptureConsole(), () => fake, CancellationToken.None, binding));
    True(fake.SourceCalled && fake.EqCalled && fake.VolumeCalled);

    var bluetoothConstructed = false;
    var mismatched = await S880Application.RunAsync(
        ["source", "get", "--address", "02:00:00:00:00:02", "--address-type", "public", "--output", "source.json"],
        new CaptureConsole(),
        () => { bluetoothConstructed = true; throw new InvalidOperationException("Bluetooth must not be constructed"); },
        CancellationToken.None,
        binding);
    Equal(ExitCodes.ProtocolNotVerified, mismatched);
    False(bluetoothConstructed);

    var randomType = await S880Application.RunAsync(
        ["eq", "get", "--address", binding.AddressText, "--address-type", "random", "--output", "eq.json"],
        new CaptureConsole(),
        () => { bluetoothConstructed = true; throw new InvalidOperationException("Bluetooth must not be constructed"); },
        CancellationToken.None,
        binding);
    Equal(ExitCodes.ProtocolNotVerified, randomType);
    False(bluetoothConstructed);
}

static async Task RawWriteIsUnavailable()
{
    var bluetoothConstructed = false;
    var console = new CaptureConsole();
    var exitCode = await S880Application.RunAsync(
        ["raw-write", "0x01"],
        console,
        () =>
        {
            bluetoothConstructed = true;
            throw new InvalidOperationException("Bluetooth must not be constructed");
        },
        CancellationToken.None);

    Equal(ExitCodes.UsageError, exitCode);
    False(bluetoothConstructed);
    Contains("unknown command", console.ErrorText);
}

static async Task AdapterDispatches()
{
    var fake = new FakeBluetoothCommands();
    var exitCode = await S880Application.RunAsync(
        ["adapter"],
        new CaptureConsole(),
        () => fake,
        CancellationToken.None);
    Equal(77, exitCode);
    True(fake.AdapterCalled);
}

static Task EdifierManufacturerIsCandidateOnly()
{
    var candidate = NativeBluetoothCommands.AssessCandidate(null, [(ushort)0x07E0]);
    False(candidate.NameMatch);
    True(candidate.EdifierManufacturerMatch);
    False(candidate.IdentityVerified);
    Contains("vendor candidate only", candidate.Reason);
    return Task.CompletedTask;
}

static async Task BleResolutionRecoversAfterExactTarget()
{
    const ulong address = 0x020000000001UL;
    var transport = new FakeBleTransport();
    transport.ResolveResult = call => Task.FromResult<FakeBleDevice?>(call == 1 ? null : new("target"));
    transport.Session.OnStart = session => session.Emit(new(address, BluetoothAddressKind.Public));
    var resolver = new BleDeviceResolver<FakeBleDevice>(transport, TimeSpan.FromSeconds(1));

    var result = await resolver.ResolveAsync(address, BluetoothAddressKind.Public, CancellationToken.None);

    Equal("target", result.Device?.Name);
    True(result.Report.Resolved);
    True(result.Report.RecoveryAttempted);
    Equal("not-found", result.Report.InitialResolution);
    True(result.Report.TargetAdvertisementObserved);
    Equal(2, result.Report.ResolutionAttempts);
    Equal(2, transport.ResolveCalls);
    Equal(1, transport.CreateSessionCalls);
    True(transport.Session.StopCalled);
    True(transport.Session.Disposed);
    Equal(0, transport.Session.ReceivedSubscriberCount);
    Equal(0, transport.Session.StoppedSubscriberCount);
}

static async Task BleResolutionIgnoresWrongTargets()
{
    const ulong address = 0x020000000001UL;
    var transport = new FakeBleTransport();
    transport.ResolveResult = call => Task.FromResult<FakeBleDevice?>(call == 1 ? null : new("target"));
    transport.Session.OnStart = session =>
    {
        session.Emit(new(0x020000000002UL, BluetoothAddressKind.Public));
        session.Emit(new(address, BluetoothAddressKind.Random));
        Equal(1, transport.ResolveCalls);
        session.Emit(new(address, BluetoothAddressKind.Public));
    };
    var resolver = new BleDeviceResolver<FakeBleDevice>(transport, TimeSpan.FromSeconds(1));

    var result = await resolver.ResolveAsync(address, BluetoothAddressKind.Public, CancellationToken.None);

    True(result.Report.Resolved);
    True(result.Report.TargetAdvertisementObserved);
    Equal(2, transport.ResolveCalls);
}

static async Task BleResolutionTimeoutCleansUp()
{
    var transport = new FakeBleTransport { ResolveResult = _ => Task.FromResult<FakeBleDevice?>(null) };
    var resolver = new BleDeviceResolver<FakeBleDevice>(transport, TimeSpan.FromMilliseconds(25));
    using var overallRequestBudget = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

    var result = await resolver.ResolveAsync(0x020000000001UL, BluetoothAddressKind.Public, overallRequestBudget.Token);

    Equal(null, result.Device);
    Equal("target-advertisement-timeout", result.Report.ErrorCode);
    Equal("target-not-observed", result.Report.ReadyState);
    False(overallRequestBudget.IsCancellationRequested);
    True(transport.Session.StopCalled);
    True(transport.Session.Disposed);
    Equal(0, transport.Session.ReceivedSubscriberCount);
    Equal(0, transport.Session.StoppedSubscriberCount);
    Equal(1, transport.ResolveCalls);
}

static async Task BleResolutionAdapterOff()
{
    var transport = new FakeBleTransport
    {
        ResolveResult = _ => Task.FromResult<FakeBleDevice?>(null),
        Readiness = BleAdapterReadiness.Off("off")
    };
    var resolver = new BleDeviceResolver<FakeBleDevice>(transport, TimeSpan.FromSeconds(1));

    var result = await resolver.ResolveAsync(0x020000000001UL, BluetoothAddressKind.Public, CancellationToken.None);

    Equal("adapter-off", result.Report.ErrorCode);
    Equal("off", result.Report.AdapterState);
    False(result.Report.DiscoveryStarted);
    Equal(0, transport.CreateSessionCalls);
    Equal(1, transport.ResolveCalls);
}

static async Task BleResolutionCancellationCleansUp()
{
    var transport = new FakeBleTransport { ResolveResult = _ => Task.FromResult<FakeBleDevice?>(null) };
    var resolver = new BleDeviceResolver<FakeBleDevice>(transport, TimeSpan.FromSeconds(1));
    using var cancellation = new CancellationTokenSource();

    var pending = resolver.ResolveAsync(0x020000000001UL, BluetoothAddressKind.Public, cancellation.Token);
    await transport.Session.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
    cancellation.Cancel();
    var result = await pending;

    Equal("request-cancelled", result.Report.ErrorCode);
    Equal("cancelled", result.Report.ReadyState);
    True(transport.Session.StopCalled);
    True(transport.Session.Disposed);
    Equal(0, transport.Session.ReceivedSubscriberCount);
    Equal(0, transport.Session.StoppedSubscriberCount);
    Equal(1, transport.ResolveCalls);
}

static async Task BleResolutionRejectsConcurrentAttempt()
{
    var firstResolution = new TaskCompletionSource<FakeBleDevice?>(TaskCreationOptions.RunContinuationsAsynchronously);
    var transport = new FakeBleTransport { ResolveResult = _ => firstResolution.Task };
    var resolver = new BleDeviceResolver<FakeBleDevice>(transport, TimeSpan.FromSeconds(1));

    var first = resolver.ResolveAsync(0x020000000001UL, BluetoothAddressKind.Public, CancellationToken.None);
    await transport.FirstResolveStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
    var concurrent = await resolver.ResolveAsync(0x020000000001UL, BluetoothAddressKind.Public, CancellationToken.None);

    Equal("resolver-busy", concurrent.Report.ErrorCode);
    Equal(1, transport.ResolveCalls);
    firstResolution.SetResult(new("target"));
    var completed = await first;
    True(completed.Report.Resolved);
    Equal(0, transport.CreateSessionCalls);
}

static Task BleConnectionRetryabilityIsStable()
{
    foreach (var failureKind in new[]
    {
        "adapter-unavailable",
        "adapter-off",
        "target-advertisement-timeout",
        "target-advertised-unresolved",
        "discovery-failed",
        "resolution-failed",
        "resolution-timeout",
        "gatt-communication-failed",
        "gatt-communication-timeout"
    })
    {
        True(NativeBluetoothCommands.IsRetryableConnectionFailure("error", failureKind));
        Equal("connection", NativeBluetoothCommands.ConnectionErrorCategory("error", failureKind));
    }

    foreach (var failureKind in new[]
    {
        "low-energy-unsupported",
        "central-role-unsupported",
        "adapter-access-denied",
        "gatt-access-denied",
        "request-cancelled",
        "resolver-busy"
    })
    {
        False(NativeBluetoothCommands.IsRetryableConnectionFailure("error", failureKind));
    }

    False(NativeBluetoothCommands.IsRetryableConnectionFailure("outcome-uncertain", "gatt-communication-failed"));
    Equal(null, NativeBluetoothCommands.ConnectionFailureKind("outcome-uncertain", "gatt-communication-failed"));
    Equal("unsupported", NativeBluetoothCommands.ConnectionErrorCategory("error", "low-energy-unsupported"));
    Equal("permission", NativeBluetoothCommands.ConnectionErrorCategory("error", "adapter-access-denied"));
    Equal("permission", NativeBluetoothCommands.ConnectionErrorCategory("error", "gatt-access-denied"));
    Equal("gatt-access-denied", NativeBluetoothCommands.GattFailureKind(Windows.Devices.Bluetooth.GenericAttributeProfile.GattCommunicationStatus.AccessDenied));
    Equal("cancelled", NativeBluetoothCommands.ConnectionErrorCategory("cancelled", "request-cancelled"));
    Equal("busy", NativeBluetoothCommands.ConnectionErrorCategory("error", "resolver-busy"));

    var cancelledResolution = new BleConnectionReport(
        "02:00:00:00:00:01", "public", "fast-address-resolution", "cancelled", "request-cancelled",
        "cancelled", false, "not-completed", null, false, false, 10, 1, false, null);
    var timedOutResolution = NativeBluetoothCommands.ConnectionTimedOut(cancelledResolution);
    Equal("resolution-timeout", timedOutResolution?.ErrorCode);
    True(NativeBluetoothCommands.IsRetryableConnectionFailure("timeout", timedOutResolution?.ErrorCode));

    var readyResolution = cancelledResolution with
    {
        Stage = "ready",
        ReadyState = "ready",
        ErrorCode = null,
        Message = null,
        Resolved = true
    };
    Equal("gatt", NativeBluetoothCommands.ConnectionStage(readyResolution, "gatt-communication-failed"));
    Equal("target-discovery", NativeBluetoothCommands.ConnectionStage(cancelledResolution with { Stage = "target-discovery" }, null));
    Equal("adapter-unavailable", BleAdapterReadiness.MissingRadio().ErrorCode);
    return Task.CompletedTask;
}

static Task CapturedOutboundVectors()
{
    Equal("AAECC900005F", Convert.ToHexString(EdifierS880Mk2CnProtocol.ProductIdentityQuery));
    Equal("AAEC610000F7", Convert.ToHexString(EdifierS880Mk2CnProtocol.SourceQuery));
    Equal("AAEC62000210010B", Convert.ToHexString(EdifierS880Mk2CnProtocol.SourceSet(SourceOperation.Usb)));
    Equal("AAEC62000210020C", Convert.ToHexString(EdifierS880Mk2CnProtocol.SourceSet(SourceOperation.LineIn1)));
    Equal("AAEC62000210030D", Convert.ToHexString(EdifierS880Mk2CnProtocol.SourceSet(SourceOperation.LineIn2)));
    Equal("AAEC62000210040E", Convert.ToHexString(EdifierS880Mk2CnProtocol.SourceSet(SourceOperation.Bluetooth)));
    Equal("AAEC62000210050F", Convert.ToHexString(EdifierS880Mk2CnProtocol.SourceSet(SourceOperation.Optical)));
    Equal("AAEC620002100610", Convert.ToHexString(EdifierS880Mk2CnProtocol.SourceSet(SourceOperation.Coaxial)));
    Equal("AAECD500006B", Convert.ToHexString(EdifierS880Mk2CnEqProtocol.PresetQuery));
    Equal("AAEC430000D9", Convert.ToHexString(EdifierS880Mk2CnEqProtocol.CustomQuery));
    Equal("AAECC40001005B", Convert.ToHexString(EdifierS880Mk2CnEqProtocol.PresetSet(EqPreset.Classic)));
    Equal("AAECC40001015C", Convert.ToHexString(EdifierS880Mk2CnEqProtocol.PresetSet(EqPreset.Monitor)));
    Equal("AAECC40001025D", Convert.ToHexString(EdifierS880Mk2CnEqProtocol.PresetSet(EqPreset.Dynamic)));
    Equal("AAECC40001035E", Convert.ToHexString(EdifierS880Mk2CnEqProtocol.PresetSet(EqPreset.Vocal)));
    Equal("AAECC40001045F", Convert.ToHexString(EdifierS880Mk2CnEqProtocol.PresetSet(EqPreset.Custom)));
    Equal("AAEC660000FC", Convert.ToHexString(EdifierS880Mk2CnVolumeProtocol.Query));
    Equal("AAEC67000100FE", Convert.ToHexString(EdifierS880Mk2CnVolumeProtocol.Set(0)));
    Equal("AAEC6700010B09", Convert.ToHexString(EdifierS880Mk2CnVolumeProtocol.Set(11)));
    Equal("AAEC6700011E1C", Convert.ToHexString(EdifierS880Mk2CnVolumeProtocol.Set(30)));
    return Task.CompletedTask;
}

static async Task EqPresetQueryDecodesKnownValue()
{
    var exchange = new ScriptedExchange();
    exchange.OnWrite = frame =>
    {
        switch (Convert.ToHexString(frame))
        {
            case "AAECC900005F":
                exchange.Push(Incoming(0xC9, Encoding.ASCII.GetBytes("EDIFIER S880 MKII")));
                break;
            case "AAECD500006B":
                exchange.Push(Concat(Incoming(0x61, [0x10, 0x04]), Incoming(0xD5, [0x00])));
                break;
        }
        return Task.CompletedTask;
    };

    var outcome = await EcEqController.ExecuteAsync(EqOperation.Get, exchange, CancellationToken.None);
    Equal("get", outcome.Operation);
    Equal("AAECD500006B", outcome.QueryFrameHex);
    Equal("BBECD50001007D", outcome.ResponseFrameHex);
    Equal("00", outcome.RawPayloadHex);
    Equal((byte?)0x00, outcome.PresetCode);
    Equal("classic", outcome.PresetName);
    True(outcome.SemanticDecoded);
    Equal("AAECC900005F,AAECD500006B", string.Join(",", exchange.WritesHex));
}

static async Task CustomEqQueryPreservesRawPayload()
{
    var exchange = new ScriptedExchange();
    exchange.OnWrite = frame =>
    {
        switch (Convert.ToHexString(frame))
        {
            case "AAECC900005F":
                exchange.Push(Incoming(0xC9, Encoding.ASCII.GetBytes("EDIFIER S880 MKII")));
                break;
            case "AAEC430000D9":
                exchange.Push(Incoming(0x43, [0x0A, 0x01, 0xAA, 0x55]));
                break;
        }
        return Task.CompletedTask;
    };

    var outcome = await EcEqController.ExecuteAsync(EqOperation.CustomGet, exchange, CancellationToken.None);
    Equal("custom-get", outcome.Operation);
    Equal("0A01AA55", outcome.RawPayloadHex);
    Equal((byte?)0x0A, outcome.CustomFormatByte);
    Equal(null, outcome.PresetName);
    False(outcome.SemanticDecoded);
    Contains("uninterpreted", outcome.Interpretation);
    Equal("AAECC900005F,AAEC430000D9", string.Join(",", exchange.WritesHex));
}

static async Task CustomEqQueryDecodesValidatedFormat03()
{
    var payload = CustomEqFixture();
    var exchange = new ScriptedExchange();
    exchange.OnWrite = frame =>
    {
        var hex = Convert.ToHexString(frame);
        if (hex == "AAECC900005F") exchange.Push(Incoming(0xC9, Encoding.ASCII.GetBytes("EDIFIER S880 MKII")));
        if (hex == "AAEC430000D9") exchange.Push(Incoming(0x43, payload));
        return Task.CompletedTask;
    };

    var outcome = await EcEqController.ExecuteAsync(EqOperation.CustomGet, exchange, CancellationToken.None);
    True(outcome.SemanticDecoded);
    Equal("validated-format-03-six-band-custom-eq", outcome.Interpretation);
    var snapshot = outcome.CustomSnapshot ?? throw new InvalidOperationException("Expected decoded Custom EQ snapshot");
    Equal((byte)0x03, snapshot.Format);
    Equal((byte)0x06, snapshot.Count);
    Equal("62,250,1000,4000,8000,16000", string.Join(",", snapshot.Bands.Select(band => band.FrequencyHz)));
    Equal("0.0,0.0,0.0,0.0,0.0,0.0", string.Join(",", snapshot.Bands.Select(band => band.GainDb.ToString("0.0"))));
    Equal("7,7,7,7,7,7", string.Join(",", snapshot.Bands.Select(band => band.QRaw)));
    Equal("68554E6A536F756E642065666665637473", Convert.ToHexString(snapshot.OpaqueTail));
}

static Task CustomEqGainConversionBoundaries()
{
    Equal((byte)0, EdifierS880Mk2CnCustomEqProtocol.GainDbToRaw(-3m));
    Equal((byte)6, EdifierS880Mk2CnCustomEqProtocol.GainDbToRaw(0m));
    Equal((byte)7, EdifierS880Mk2CnCustomEqProtocol.GainDbToRaw(0.5m));
    Equal((byte)12, EdifierS880Mk2CnCustomEqProtocol.GainDbToRaw(3m));
    Equal(-3m, EdifierS880Mk2CnCustomEqProtocol.RawGainToDb(0));
    Equal(0.5m, EdifierS880Mk2CnCustomEqProtocol.RawGainToDb(7));
    Equal(3m, EdifierS880Mk2CnCustomEqProtocol.RawGainToDb(12));
    Throws<ArgumentOutOfRangeException>(() => EdifierS880Mk2CnCustomEqProtocol.GainDbToRaw(1.25m));
    Throws<ArgumentOutOfRangeException>(() => EdifierS880Mk2CnCustomEqProtocol.GainDbToRaw(3.5m));
    return Task.CompletedTask;
}

static async Task CustomEqBandSetRoundTrip()
{
    var before = CustomEqFixture();
    var after = before.ToArray();
    after[18] = 7;
    var customQueryCount = 0;
    var presetQueryCount = 0;
    var exchange = new ScriptedExchange();
    exchange.OnWrite = frame =>
    {
        var hex = Convert.ToHexString(frame);
        if (hex == "AAECC900005F") exchange.Push(Incoming(0xC9, Encoding.ASCII.GetBytes("EDIFIER S880 MKII")));
        else if (hex == "AAECD500006B")
        {
            presetQueryCount++;
            exchange.Push(Incoming(0xD5, [presetQueryCount == 1 ? (byte)0x00 : (byte)0x04]));
        }
        else if (hex == "AAEC430000D9")
        {
            customQueryCount++;
            exchange.Push(Incoming(0x43, customQueryCount == 1 ? before : after));
        }
        else if (hex == "AAEC440006020003E80707DB")
        {
            True(exchange.SetAttempted);
            exchange.Push(Incoming(0x44, [0x00]));
        }
        return Task.CompletedTask;
    };

    var outcome = await EcCustomEqGainController.ExecuteAsync(3, 0.5m, exchange, CancellationToken.None);
    Equal(3, outcome.Band);
    Equal((ushort)1000, outcome.FrequencyHz);
    Equal((byte)7, outcome.RequestedGainRaw);
    Equal(0.5m, outcome.RequestedGainDb);
    Equal((byte)6, outcome.Before.Bands[2].GainRaw);
    Equal((byte)7, outcome.After.Bands[2].GainRaw);
    Equal("68554E6A536F756E642065666665637473", Convert.ToHexString(outcome.After.OpaqueTail));
    Equal("AAEC440006020003E80707DB", outcome.SetFrameHex);
    Equal((byte?)0x00, outcome.AckPayloadByte);
    Equal("classic", outcome.PresetBeforeName);
    Equal("custom", outcome.PresetAfterName);
    True(outcome.FirmwareChangedPreset);
    Equal("complete-custom-payload-readback-only-requested-gain-changed", outcome.Verification);
    Equal("AAECC900005F,AAECD500006B,AAEC430000D9,AAEC440006020003E80707DB,AAEC430000D9,AAECD500006B", string.Join(",", exchange.WritesHex));
    False(exchange.WritesHex.Any(frame => frame.StartsWith("AAECC4", StringComparison.Ordinal)));
}

static async Task CustomEqGainAlreadyCurrent()
{
    var current = CustomEqFixture(7);
    var exchange = new ScriptedExchange();
    exchange.OnWrite = frame =>
    {
        var hex = Convert.ToHexString(frame);
        if (hex == "AAECC900005F") exchange.Push(Incoming(0xC9, Encoding.ASCII.GetBytes("EDIFIER S880 MKII")));
        if (hex == "AAECD500006B") exchange.Push(Incoming(0xD5, [0x00]));
        if (hex == "AAEC430000D9") exchange.Push(Incoming(0x43, current));
        return Task.CompletedTask;
    };

    var outcome = await EcCustomEqGainController.ExecuteAsync(3, 0.5m, exchange, CancellationToken.None);
    Equal("already-current-gain", outcome.Verification);
    Equal(null, outcome.SetFrameHex);
    Equal("AAECC900005F,AAECD500006B,AAEC430000D9", string.Join(",", exchange.WritesHex));
    False(exchange.SetAttempted);
}

static async Task UnsupportedCustomEqFormatBlocksWrite()
{
    var payload = CustomEqFixture();
    payload[0] = 0x04;
    var exchange = CustomEqPreflightExchange(payload);
    await ThrowsAsync<EqProtocolException>(() =>
        EcCustomEqGainController.ExecuteAsync(3, 0.5m, exchange, CancellationToken.None));
    Equal("AAECC900005F,AAECD500006B,AAEC430000D9", string.Join(",", exchange.WritesHex));
    False(exchange.SetAttempted);
}

static async Task MismatchedCustomEqIndexBlocksWrite()
{
    var payload = CustomEqFixture();
    payload[14] = 0x03;
    var exchange = CustomEqPreflightExchange(payload);
    await ThrowsAsync<EqProtocolException>(() =>
        EcCustomEqGainController.ExecuteAsync(3, 0.5m, exchange, CancellationToken.None));
    False(exchange.SetAttempted);
}

static async Task UnexpectedCustomEqMetadataChangeFails()
{
    var before = CustomEqFixture();
    var after = before.ToArray();
    after[18] = 7;
    after[38] ^= 0x01;
    var customQueryCount = 0;
    var exchange = new ScriptedExchange();
    exchange.OnWrite = frame =>
    {
        var hex = Convert.ToHexString(frame);
        if (hex == "AAECC900005F") exchange.Push(Incoming(0xC9, Encoding.ASCII.GetBytes("EDIFIER S880 MKII")));
        else if (hex == "AAECD500006B") exchange.Push(Incoming(0xD5, [0x00]));
        else if (hex == "AAEC430000D9")
        {
            customQueryCount++;
            exchange.Push(Incoming(0x43, customQueryCount == 1 ? before : after));
        }
        else if (hex == "AAEC440006020003E80707DB") exchange.Push(Incoming(0x44, [0x00]));
        return Task.CompletedTask;
    };

    await ThrowsAsync<EqProtocolException>(() =>
        EcCustomEqGainController.ExecuteAsync(3, 0.5m, exchange, CancellationToken.None));
    True(exchange.SetAttempted);
    Equal("AAECC900005F,AAECD500006B,AAEC430000D9,AAEC440006020003E80707DB,AAEC430000D9", string.Join(",", exchange.WritesHex));
}

static async Task CustomEqCancellationPreservesPreflight()
{
    using var cancellation = new CancellationTokenSource();
    var before = CustomEqFixture(8);
    var trace = new CustomEqGainTrace();
    var exchange = new ScriptedExchange();
    exchange.OnWrite = frame =>
    {
        var hex = Convert.ToHexString(frame);
        if (hex == "AAECC900005F") exchange.Push(Incoming(0xC9, Encoding.ASCII.GetBytes("EDIFIER S880 MKII")));
        else if (hex == "AAECD500006B") exchange.Push(Incoming(0xD5, [0x00]));
        else if (hex == "AAEC430000D9") exchange.Push(Incoming(0x43, before));
        else if (hex == "AAEC440006020003E80707DB") cancellation.Cancel();
        return Task.CompletedTask;
    };

    await ThrowsAsync<OperationCanceledException>(() =>
        EcCustomEqGainController.ExecuteAsync(3, 0.5m, exchange, cancellation.Token, trace));
    True(exchange.SetAttempted);
    Equal((byte?)8, trace.Before?.Bands[2].GainRaw);
    Equal("68554E6A536F756E642065666665637473", Convert.ToHexString(trace.Before?.OpaqueTail ?? []));
    Equal("AAEC440006020003E80707DB", trace.SetFrameHex);
    Equal("AAECC900005F,AAECD500006B,AAEC430000D9,AAEC440006020003E80707DB", string.Join(",", exchange.WritesHex));
}

static async Task UnexpectedPresetPayloadRemainsRaw()
{
    var exchange = new ScriptedExchange();
    exchange.OnWrite = frame =>
    {
        switch (Convert.ToHexString(frame))
        {
            case "AAECC900005F": exchange.Push(Incoming(0xC9, Encoding.ASCII.GetBytes("EDIFIER S880 MKII"))); break;
            case "AAECD500006B": exchange.Push(Incoming(0xD5, [0x00, 0x01, 0x02])); break;
        }
        return Task.CompletedTask;
    };

    var outcome = await EcEqController.ExecuteAsync(EqOperation.Get, exchange, CancellationToken.None);
    Equal("000102", outcome.RawPayloadHex);
    Equal(null, outcome.PresetCode);
    Equal(null, outcome.PresetName);
    False(outcome.SemanticDecoded);
    Contains("without-calibration-inference", outcome.Interpretation);
}

static async Task WrongIdentityBlocksEqQuery()
{
    var exchange = new ScriptedExchange();
    exchange.OnWrite = frame =>
    {
        exchange.Push(Incoming(0xC9, Encoding.ASCII.GetBytes("NOT THE TARGET")));
        return Task.CompletedTask;
    };

    await ThrowsAsync<EqProtocolException>(() =>
        EcEqController.ExecuteAsync(EqOperation.Get, exchange, CancellationToken.None));
    Equal("AAECC900005F", string.Join(",", exchange.WritesHex));
}

static async Task BadEqResponseTimesOutWithoutRetry()
{
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
    var exchange = new ScriptedExchange();
    exchange.OnWrite = frame =>
    {
        switch (Convert.ToHexString(frame))
        {
            case "AAECC900005F":
                exchange.Push(Incoming(0xC9, Encoding.ASCII.GetBytes("EDIFIER S880 MKII")));
                break;
            case "AAECD500006B":
                var bad = Incoming(0xD5, [0x00]);
                bad[^1] ^= 0xFF;
                exchange.Push(bad);
                break;
        }
        return Task.CompletedTask;
    };

    await ThrowsAsync<OperationCanceledException>(() =>
        EcEqController.ExecuteAsync(EqOperation.Get, exchange, cancellation.Token));
    Equal("AAECC900005F,AAECD500006B", string.Join(",", exchange.WritesHex));
}

static async Task EqPresetSetRoundTrip()
{
    var exchange = new ScriptedExchange();
    var queryCount = 0;
    exchange.OnWrite = frame =>
    {
        switch (Convert.ToHexString(frame))
        {
            case "AAECC900005F":
                exchange.Push(Incoming(0xC9, Encoding.ASCII.GetBytes("EDIFIER S880 MKII")));
                break;
            case "AAECD500006B":
                queryCount++;
                exchange.Push(Incoming(0xD5, [queryCount == 1 ? (byte)0x00 : (byte)0x01]));
                break;
            case "AAECC40001015C":
                True(exchange.SetAttempted);
                exchange.Push(Incoming(0xC4, [0x01]));
                break;
        }
        return Task.CompletedTask;
    };

    var outcome = await EcEqController.ExecuteAsync(
        EqOperation.Set, exchange, CancellationToken.None, EqPreset.Monitor);
    Equal((byte?)0x00, outcome.CurrentBeforeCode);
    Equal("classic", outcome.CurrentBeforeName);
    Equal((byte?)0x01, outcome.RequestedPresetCode);
    Equal("monitor", outcome.RequestedPresetName);
    Equal("AAECC40001015C", outcome.SetFrameHex);
    Equal((byte?)0x01, outcome.AckPayloadByte);
    Contains("uninterpreted", outcome.AckInterpretation);
    Equal((byte?)0x01, outcome.CurrentAfterCode);
    Equal("monitor", outcome.CurrentAfterName);
    Equal("explicit-preset-query-after-ack", outcome.Verification);
    Equal("AAECC900005F,AAECD500006B,AAECC40001015C,AAECD500006B", string.Join(",", exchange.WritesHex));
    True(exchange.SetAttempted);
}

static async Task EqPresetAlreadyCurrent()
{
    var exchange = new ScriptedExchange();
    exchange.OnWrite = frame =>
    {
        switch (Convert.ToHexString(frame))
        {
            case "AAECC900005F": exchange.Push(Incoming(0xC9, Encoding.ASCII.GetBytes("EDIFIER S880 MKII"))); break;
            case "AAECD500006B": exchange.Push(Incoming(0xD5, [0x01])); break;
        }
        return Task.CompletedTask;
    };

    var outcome = await EcEqController.ExecuteAsync(
        EqOperation.Set, exchange, CancellationToken.None, EqPreset.Monitor);
    Equal("already-current", outcome.Verification);
    Equal(null, outcome.SetFrameHex);
    Equal("AAECC900005F,AAECD500006B", string.Join(",", exchange.WritesHex));
    False(exchange.SetAttempted);
}

static async Task UnknownInitialEqBlocksSet()
{
    var exchange = new ScriptedExchange();
    exchange.OnWrite = frame =>
    {
        switch (Convert.ToHexString(frame))
        {
            case "AAECC900005F": exchange.Push(Incoming(0xC9, Encoding.ASCII.GetBytes("EDIFIER S880 MKII"))); break;
            case "AAECD500006B": exchange.Push(Incoming(0xD5, [0x00, 0x01])); break;
        }
        return Task.CompletedTask;
    };

    await ThrowsAsync<EqProtocolException>(() =>
        EcEqController.ExecuteAsync(EqOperation.Set, exchange, CancellationToken.None, EqPreset.Monitor));
    Equal("AAECC900005F,AAECD500006B", string.Join(",", exchange.WritesHex));
    False(exchange.SetAttempted);
}

static async Task AckZeroWithMatchingReadbackPasses()
{
    var exchange = new ScriptedExchange();
    var queryCount = 0;
    exchange.OnWrite = frame =>
    {
        switch (Convert.ToHexString(frame))
        {
            case "AAECC900005F": exchange.Push(Incoming(0xC9, Encoding.ASCII.GetBytes("EDIFIER S880 MKII"))); break;
            case "AAECD500006B":
                queryCount++;
                exchange.Push(Incoming(0xD5, [queryCount == 1 ? (byte)0x01 : (byte)0x00]));
                break;
            case "AAECC40001005B": exchange.Push(Incoming(0xC4, [0x00])); break;
        }
        return Task.CompletedTask;
    };

    var outcome = await EcEqController.ExecuteAsync(
        EqOperation.Set, exchange, CancellationToken.None, EqPreset.Classic);
    Equal((byte?)0x00, outcome.AckPayloadByte);
    Equal((byte?)0x00, outcome.CurrentAfterCode);
    Equal("classic", outcome.CurrentAfterName);
    Equal("explicit-preset-query-after-ack", outcome.Verification);
    Equal("AAECC900005F,AAECD500006B,AAECC40001005B,AAECD500006B", string.Join(",", exchange.WritesHex));
    True(exchange.SetAttempted);
}

static async Task AckZeroWithOldReadbackFails()
{
    var exchange = new ScriptedExchange();
    exchange.OnWrite = frame =>
    {
        switch (Convert.ToHexString(frame))
        {
            case "AAECC900005F": exchange.Push(Incoming(0xC9, Encoding.ASCII.GetBytes("EDIFIER S880 MKII"))); break;
            case "AAECD500006B": exchange.Push(Incoming(0xD5, [0x00])); break;
            case "AAECC40001015C": exchange.Push(Incoming(0xC4, [0x00])); break;
        }
        return Task.CompletedTask;
    };

    await ThrowsAsync<EqProtocolException>(() =>
        EcEqController.ExecuteAsync(EqOperation.Set, exchange, CancellationToken.None, EqPreset.Monitor));
    Equal("AAECC900005F,AAECD500006B,AAECC40001015C,AAECD500006B", string.Join(",", exchange.WritesHex));
    True(exchange.SetAttempted);
}

static async Task MalformedEqAckDoesNotContinue()
{
    var exchange = new ScriptedExchange();
    exchange.OnWrite = frame =>
    {
        switch (Convert.ToHexString(frame))
        {
            case "AAECC900005F": exchange.Push(Incoming(0xC9, Encoding.ASCII.GetBytes("EDIFIER S880 MKII"))); break;
            case "AAECD500006B": exchange.Push(Incoming(0xD5, [0x00])); break;
            case "AAECC40001015C": exchange.Push(Incoming(0xC4, [0x00, 0x01])); break;
        }
        return Task.CompletedTask;
    };

    await ThrowsAsync<EqProtocolException>(() =>
        EcEqController.ExecuteAsync(EqOperation.Set, exchange, CancellationToken.None, EqPreset.Monitor));
    Equal("AAECC900005F,AAECD500006B,AAECC40001015C", string.Join(",", exchange.WritesHex));
    True(exchange.SetAttempted);
}

static async Task MissingEqReadbackDoesNotRetry()
{
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
    var exchange = new ScriptedExchange();
    var queryCount = 0;
    exchange.OnWrite = frame =>
    {
        switch (Convert.ToHexString(frame))
        {
            case "AAECC900005F": exchange.Push(Incoming(0xC9, Encoding.ASCII.GetBytes("EDIFIER S880 MKII"))); break;
            case "AAECD500006B":
                queryCount++;
                if (queryCount == 1) exchange.Push(Incoming(0xD5, [0x00]));
                break;
            case "AAECC40001015C": exchange.Push(Incoming(0xC4, [0x01])); break;
        }
        return Task.CompletedTask;
    };

    await ThrowsAsync<OperationCanceledException>(() =>
        EcEqController.ExecuteAsync(EqOperation.Set, exchange, cancellation.Token, EqPreset.Monitor));
    Equal("AAECC900005F,AAECD500006B,AAECC40001015C,AAECD500006B", string.Join(",", exchange.WritesHex));
    True(exchange.SetAttempted);
}

static async Task EqCancellationBeforeSet()
{
    using var cancellation = new CancellationTokenSource();
    var exchange = new ScriptedExchange();
    exchange.OnWrite = frame =>
    {
        switch (Convert.ToHexString(frame))
        {
            case "AAECC900005F": exchange.Push(Incoming(0xC9, Encoding.ASCII.GetBytes("EDIFIER S880 MKII"))); break;
            case "AAECD500006B":
                exchange.Push(Incoming(0xD5, [0x00]));
                cancellation.Cancel();
                break;
        }
        return Task.CompletedTask;
    };

    await ThrowsAsync<OperationCanceledException>(() =>
        EcEqController.ExecuteAsync(EqOperation.Set, exchange, cancellation.Token, EqPreset.Monitor));
    Equal("AAECC900005F,AAECD500006B", string.Join(",", exchange.WritesHex));
    False(exchange.SetAttempted);
}

static Task VolumeTargetsParse()
{
    var getResult = CliParser.Parse([
        "volume", "get", "--address", "02:00:00:00:00:01", "--address-type", "public",
        "--output", "volume.json", "--timeout-seconds", "60"]);
    var get = AssertType<VolumeCommand>(getResult.Command);
    Equal(VolumeOperation.Get, get.Operation);
    Equal(null, get.Value);
    Equal(60, get.TimeoutSeconds);

    foreach (var level in new[] { 0, 1, 30 })
    {
        var setResult = CliParser.Parse([
            "volume", "set", level.ToString(), "--address", "02:00:00:00:00:01", "--address-type", "public", "--output", "volume.json"]);
        var set = AssertType<VolumeCommand>(setResult.Command);
        Equal(VolumeOperation.Set, set.Operation);
        Equal((int?)level, set.Value);
    }

    False(CliParser.Parse([
        "volume", "set", "31", "--address", "02:00:00:00:00:01", "--address-type", "public", "--output", "volume.json"]).IsSuccess);
    False(CliParser.Parse([
        "volume", "set", "-1", "--address", "02:00:00:00:00:01", "--address-type", "public", "--output", "volume.json"]).IsSuccess);
    return Task.CompletedTask;
}

static async Task UnknownVolumeActionFailsBeforeBluetooth()
{
    var bluetoothConstructed = false;
    var exitCode = await S880Application.RunAsync(
        ["volume", "mute"],
        new CaptureConsole(),
        () =>
        {
            bluetoothConstructed = true;
            throw new InvalidOperationException("Bluetooth must not be constructed");
        },
        CancellationToken.None);
    Equal(ExitCodes.UsageError, exitCode);
    False(bluetoothConstructed);
}

static async Task UnboundVolumeFailsBeforeBluetooth()
{
    var bluetoothConstructed = false;
    var exitCode = await S880Application.RunAsync(
        ["volume", "get", "--address", "AA:BB:CC:DD:EE:FF", "--address-type", "public", "--output", "volume.json"],
        new CaptureConsole(),
        () =>
        {
            bluetoothConstructed = true;
            throw new InvalidOperationException("Bluetooth must not be constructed");
        },
        CancellationToken.None,
        SpeakerTargetBinding.Unconfigured);
    Equal(ExitCodes.ProtocolNotVerified, exitCode);
    False(bluetoothConstructed);
}

static async Task VolumeQueryDecodesMaxAndCurrent()
{
    var exchange = new ScriptedExchange();
    exchange.OnWrite = frame =>
    {
        var hex = Convert.ToHexString(frame);
        if (hex == "AAECC900005F") exchange.Push(Incoming(0xC9, Encoding.ASCII.GetBytes("EDIFIER S880 MKII")));
        if (hex == "AAEC660000FC") exchange.Push(Concat(Incoming(0x61, [0x10, 0x04]), Incoming(0x66, [0x1E, 0x0C])));
        return Task.CompletedTask;
    };

    var outcome = await EcVolumeController.ExecuteAsync(VolumeOperation.Get, null, exchange, CancellationToken.None);
    Equal((byte)30, outcome.Maximum);
    Equal((byte)12, outcome.CurrentBefore);
    Equal((byte)12, outcome.CurrentAfter);
    Equal("BBEC6600021E0C39", outcome.CurrentAfterFrameHex);
    Equal("AAECC900005F,AAEC660000FC", string.Join(",", exchange.WritesHex));
    False(exchange.SetAttempted);
}

static async Task VolumeSetRoundTrip()
{
    var exchange = new ScriptedExchange();
    var queryCount = 0;
    exchange.OnWrite = frame =>
    {
        var hex = Convert.ToHexString(frame);
        if (hex == "AAECC900005F") exchange.Push(Incoming(0xC9, Encoding.ASCII.GetBytes("EDIFIER S880 MKII")));
        else if (hex == "AAEC660000FC")
        {
            queryCount++;
            exchange.Push(Incoming(0x66, [0x1E, queryCount == 1 ? (byte)0x0C : (byte)0x0B]));
        }
        else if (hex == "AAEC6700010B09")
        {
            True(exchange.SetAttempted);
            exchange.Push(Incoming(0x67, [0x1E, 0x0B]));
        }
        return Task.CompletedTask;
    };

    var outcome = await EcVolumeController.ExecuteAsync(VolumeOperation.Set, 11, exchange, CancellationToken.None);
    Equal((byte)30, outcome.Maximum);
    Equal((byte)12, outcome.CurrentBefore);
    Equal((int?)11, outcome.Requested);
    Equal((byte)11, outcome.CurrentAfter);
    Equal("AAEC6700010B09", outcome.SetFrameHex);
    Equal("BBEC6700021E0B39", outcome.AckFrameHex);
    Equal("explicit-volume-query-after-ack", outcome.Verification);
    Equal("AAECC900005F,AAEC660000FC,AAEC6700010B09,AAEC660000FC", string.Join(",", exchange.WritesHex));
    True(exchange.SetAttempted);
}

static async Task VolumeZeroRoundTrip()
{
    var exchange = new ScriptedExchange();
    var queryCount = 0;
    exchange.OnWrite = frame =>
    {
        var hex = Convert.ToHexString(frame);
        if (hex == "AAECC900005F") exchange.Push(Incoming(0xC9, Encoding.ASCII.GetBytes("EDIFIER S880 MKII")));
        else if (hex == "AAEC660000FC")
        {
            queryCount++;
            exchange.Push(Incoming(0x66, [0x1E, queryCount == 1 ? (byte)0x01 : (byte)0x00]));
        }
        else if (hex == "AAEC67000100FE") exchange.Push(Incoming(0x67, [0x1E, 0x00]));
        return Task.CompletedTask;
    };

    var outcome = await EcVolumeController.ExecuteAsync(VolumeOperation.Set, 0, exchange, CancellationToken.None);
    Equal((byte)0, outcome.CurrentAfter);
    Equal("AAEC67000100FE", outcome.SetFrameHex);
    True(exchange.SetAttempted);
}

static async Task VolumeAlreadyCurrent()
{
    var exchange = new ScriptedExchange();
    exchange.OnWrite = frame =>
    {
        var hex = Convert.ToHexString(frame);
        if (hex == "AAECC900005F") exchange.Push(Incoming(0xC9, Encoding.ASCII.GetBytes("EDIFIER S880 MKII")));
        if (hex == "AAEC660000FC") exchange.Push(Incoming(0x66, [0x1E, 0x0C]));
        return Task.CompletedTask;
    };
    var outcome = await EcVolumeController.ExecuteAsync(VolumeOperation.Set, 12, exchange, CancellationToken.None);
    Equal("already-current", outcome.Verification);
    Equal(null, outcome.SetFrameHex);
    False(exchange.SetAttempted);
}

static async Task WrongIdentityBlocksVolume()
{
    var exchange = new ScriptedExchange();
    exchange.OnWrite = frame =>
    {
        exchange.Push(Incoming(0xC9, Encoding.ASCII.GetBytes("NOT THE TARGET")));
        return Task.CompletedTask;
    };
    await ThrowsAsync<EqProtocolException>(() =>
        EcVolumeController.ExecuteAsync(VolumeOperation.Set, 11, exchange, CancellationToken.None));
    Equal("AAECC900005F", string.Join(",", exchange.WritesHex));
    False(exchange.SetAttempted);
}

static async Task UnexpectedVolumeRangeBlocksSet()
{
    var exchange = new ScriptedExchange();
    exchange.OnWrite = frame =>
    {
        var hex = Convert.ToHexString(frame);
        if (hex == "AAECC900005F") exchange.Push(Incoming(0xC9, Encoding.ASCII.GetBytes("EDIFIER S880 MKII")));
        if (hex == "AAEC660000FC") exchange.Push(Incoming(0x66, [0x1D, 0x0C]));
        return Task.CompletedTask;
    };
    await ThrowsAsync<VolumeProtocolException>(() =>
        EcVolumeController.ExecuteAsync(VolumeOperation.Set, 11, exchange, CancellationToken.None));
    Equal("AAECC900005F,AAEC660000FC", string.Join(",", exchange.WritesHex));
    False(exchange.SetAttempted);
}

static async Task FailedVolumeAckDoesNotRetry()
{
    var exchange = new ScriptedExchange();
    exchange.OnWrite = frame =>
    {
        var hex = Convert.ToHexString(frame);
        if (hex == "AAECC900005F") exchange.Push(Incoming(0xC9, Encoding.ASCII.GetBytes("EDIFIER S880 MKII")));
        if (hex == "AAEC660000FC") exchange.Push(Incoming(0x66, [0x1E, 0x0C]));
        if (hex == "AAEC6700010B09") exchange.Push(Incoming(0x67, [0x01]));
        return Task.CompletedTask;
    };
    await ThrowsAsync<VolumeProtocolException>(() =>
        EcVolumeController.ExecuteAsync(VolumeOperation.Set, 11, exchange, CancellationToken.None));
    Equal("AAECC900005F,AAEC660000FC,AAEC6700010B09", string.Join(",", exchange.WritesHex));
    True(exchange.SetAttempted);
}

static async Task MissingVolumeReadbackDoesNotRetry()
{
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
    var exchange = new ScriptedExchange();
    var queryCount = 0;
    exchange.OnWrite = frame =>
    {
        var hex = Convert.ToHexString(frame);
        if (hex == "AAECC900005F") exchange.Push(Incoming(0xC9, Encoding.ASCII.GetBytes("EDIFIER S880 MKII")));
        else if (hex == "AAEC660000FC")
        {
            queryCount++;
            if (queryCount == 1) exchange.Push(Incoming(0x66, [0x1E, 0x0C]));
        }
        else if (hex == "AAEC6700010B09") exchange.Push(Incoming(0x67, [0x1E, 0x0B]));
        return Task.CompletedTask;
    };
    await ThrowsAsync<OperationCanceledException>(() =>
        EcVolumeController.ExecuteAsync(VolumeOperation.Set, 11, exchange, cancellation.Token));
    Equal("AAECC900005F,AAEC660000FC,AAEC6700010B09,AAEC660000FC", string.Join(",", exchange.WritesHex));
    True(exchange.SetAttempted);
}

static async Task VolumeCancellationBeforeSet()
{
    using var cancellation = new CancellationTokenSource();
    var exchange = new ScriptedExchange();
    exchange.OnWrite = frame =>
    {
        var hex = Convert.ToHexString(frame);
        if (hex == "AAECC900005F") exchange.Push(Incoming(0xC9, Encoding.ASCII.GetBytes("EDIFIER S880 MKII")));
        if (hex == "AAEC660000FC")
        {
            exchange.Push(Incoming(0x66, [0x1E, 0x0C]));
            cancellation.Cancel();
        }
        return Task.CompletedTask;
    };
    await ThrowsAsync<OperationCanceledException>(() =>
        EcVolumeController.ExecuteAsync(VolumeOperation.Set, 11, exchange, cancellation.Token));
    Equal("AAECC900005F,AAEC660000FC", string.Join(",", exchange.WritesHex));
    False(exchange.SetAttempted);
}

static Task ParserHandlesFragmentationAndCoalescing()
{
    var parser = new EcFrameParser();
    var status = Convert.FromHexString("BBEC61000210041E");
    Equal(0, parser.Push(status.AsSpan(0, 3)).Count);
    var fragmented = parser.Push(status.AsSpan(3));
    Equal(1, fragmented.Count);
    Equal((byte)0x61, fragmented[0].Command);
    Equal("1004", Convert.ToHexString(fragmented[0].Payload));

    var coalesced = parser.Push(Concat(
        Incoming(0x68, [0x05]),
        Convert.FromHexString("BBEC620001010B"),
        Convert.FromHexString("BBEC61000210011B")));
    Equal(3, coalesced.Count);
    Equal("68,62,61", string.Join(",", coalesced.Select(frame => frame.Command.ToString("X2"))));
    return Task.CompletedTask;
}

static Task ParserRejectsBadChecksum()
{
    var parser = new EcFrameParser();
    var bad = Convert.FromHexString("BBEC610002100400");
    var valid = Convert.FromHexString("BBEC61000210041E");
    var frames = parser.Push(Concat(bad, valid));
    Equal(1, frames.Count);
    Equal(1, parser.BadChecksumCount);
    Equal("BBEC61000210041E", frames[0].RawHex);
    return Task.CompletedTask;
}

static async Task WaiterIgnoresUnrelatedCommands()
{
    var inbox = new NotificationInbox();
    using var waiter = inbox.Register(frame => frame.Command == 0x61);
    inbox.Push(Concat(Incoming(0x68, [0x05]), Convert.FromHexString("BBEC61000210041E")));
    var frame = await waiter.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(1)).Token);
    Equal((byte)0x61, frame.Command);
}

static async Task SourceSetSequence()
{
    var exchange = new ScriptedExchange();
    exchange.OnWrite = frame =>
    {
        switch (Convert.ToHexString(frame))
        {
            case "AAECC900005F": exchange.Push(Incoming(0xC9, Encoding.ASCII.GetBytes("EDIFIER S880 MKII"))); break;
            case "AAEC610000F7": exchange.Push(Convert.FromHexString("BBEC61000210041E")); break;
            case "AAEC62000210010B":
                exchange.Push(Concat(Incoming(0x68, [0x05]), Convert.FromHexString("BBEC620001010B"), Convert.FromHexString("BBEC61000210011B")));
                break;
        }
        return Task.CompletedTask;
    };

    var outcome = await EcSourceController.ExecuteAsync(SourceOperation.Usb, exchange, CancellationToken.None);
    Equal("bluetooth", outcome.CurrentBefore);
    Equal("usb", outcome.CurrentAfter);
    Equal("automatic-status-notification", outcome.Verification);
    Equal("AAECC900005F,AAEC610000F7,AAEC62000210010B", string.Join(",", exchange.WritesHex));
    True(exchange.SetAttempted);
}

static async Task SourceSetExplicitVerification()
{
    var exchange = new ScriptedExchange();
    var sourceQueries = 0;
    exchange.OnWrite = frame =>
    {
        switch (Convert.ToHexString(frame))
        {
            case "AAECC900005F": exchange.Push(Incoming(0xC9, Encoding.ASCII.GetBytes("EDIFIER S880 MKII"))); break;
            case "AAEC610000F7":
                sourceQueries++;
                exchange.Push(sourceQueries == 1
                    ? Convert.FromHexString("BBEC61000210011B")
                    : Convert.FromHexString("BBEC61000210041E"));
                break;
            case "AAEC62000210040E": exchange.Push(Convert.FromHexString("BBEC620001010B")); break;
        }
        return Task.CompletedTask;
    };

    var outcome = await EcSourceController.ExecuteAsync(
        SourceOperation.Bluetooth, exchange, CancellationToken.None, TimeSpan.Zero);
    Equal("explicit-source-query-after-ack", outcome.Verification);
    Equal("AAECC900005F,AAEC610000F7,AAEC62000210040E,AAEC610000F7", string.Join(",", exchange.WritesHex));
}

static async Task UnknownCurrentSourceBlocksSet()
{
    var exchange = new ScriptedExchange();
    exchange.OnWrite = frame =>
    {
        var hex = Convert.ToHexString(frame);
        if (hex == "AAECC900005F") exchange.Push(Incoming(0xC9, Encoding.ASCII.GetBytes("EDIFIER S880 MKII")));
        if (hex == "AAEC610000F7") exchange.Push(Incoming(0x61, [0x10, 0x07]));
        return Task.CompletedTask;
    };
    await ThrowsAsync<SourceProtocolException>(() =>
        EcSourceController.ExecuteAsync(SourceOperation.Usb, exchange, CancellationToken.None));
    Equal("AAECC900005F,AAEC610000F7", string.Join(",", exchange.WritesHex));
    False(exchange.SetAttempted);
}

static async Task WrongIdentityBlocksSource()
{
    var exchange = new ScriptedExchange();
    exchange.OnWrite = frame =>
    {
        exchange.Push(Incoming(0xC9, Encoding.ASCII.GetBytes("NOT THE TARGET")));
        return Task.CompletedTask;
    };
    await ThrowsAsync<SourceProtocolException>(() =>
        EcSourceController.ExecuteAsync(SourceOperation.Get, exchange, CancellationToken.None));
    Equal("AAECC900005F", string.Join(",", exchange.WritesHex));
    False(exchange.SetAttempted);
}

static async Task CancellationBeforeSetPreventsWrite()
{
    using var cancellation = new CancellationTokenSource();
    var exchange = new ScriptedExchange();
    exchange.OnWrite = frame =>
    {
        var hex = Convert.ToHexString(frame);
        if (hex == "AAECC900005F") exchange.Push(Incoming(0xC9, Encoding.ASCII.GetBytes("EDIFIER S880 MKII")));
        if (hex == "AAEC610000F7")
        {
            exchange.Push(Convert.FromHexString("BBEC61000210041E"));
            cancellation.Cancel();
        }
        return Task.CompletedTask;
    };

    await ThrowsAsync<OperationCanceledException>(() =>
        EcSourceController.ExecuteAsync(SourceOperation.Usb, exchange, cancellation.Token));
    Equal("AAECC900005F,AAEC610000F7", string.Join(",", exchange.WritesHex));
    False(exchange.SetAttempted);
}

static async Task CancellationAfterSetPreservesAttempt()
{
    using var cancellation = new CancellationTokenSource();
    var exchange = new ScriptedExchange();
    exchange.OnWrite = frame =>
    {
        var hex = Convert.ToHexString(frame);
        if (hex == "AAECC900005F") exchange.Push(Incoming(0xC9, Encoding.ASCII.GetBytes("EDIFIER S880 MKII")));
        if (hex == "AAEC610000F7") exchange.Push(Convert.FromHexString("BBEC61000210041E"));
        if (hex == "AAEC62000210010B") cancellation.Cancel();
        return Task.CompletedTask;
    };

    await ThrowsAsync<OperationCanceledException>(() =>
        EcSourceController.ExecuteAsync(SourceOperation.Usb, exchange, cancellation.Token));
    True(exchange.SetAttempted);
    Equal("AAECC900005F,AAEC610000F7,AAEC62000210010B", string.Join(",", exchange.WritesHex));
}

static async Task ProfileIsSanitizedAndMatchesBuild()
{
    var profilePath = Path.Combine(AppContext.BaseDirectory, "EdifierS880MK2CN.json");
    using var profile = JsonDocument.Parse(await File.ReadAllTextAsync(profilePath));
    var root = profile.RootElement;

    Equal("EdifierS880MK2CN", root.GetProperty("profileId").GetString());
    Equal("EDIFIER S880 MKII", root.GetProperty("model").GetString());

    var boundDevice = root.GetProperty("boundDevice");
    Equal(SpeakerTargetBinding.Current.AddressText, boundDevice.GetProperty("address").GetString());
    Equal("public", boundDevice.GetProperty("addressType").GetString());

    Equal("C9", root.GetProperty("identityQuery").GetProperty("command").GetString());
    Equal("EDIFIER S880 MKII", root.GetProperty("identityQuery").GetProperty("expectedAsciiPayload").GetString());
    Equal(1, root.GetProperty("serviceUuids").GetArrayLength());
    Equal(2, root.GetProperty("characteristicUuids").GetArrayLength());

    var protocol = root.GetProperty("protocol");
    Equal("61", protocol.GetProperty("source").GetProperty("queryCommand").GetString());
    Equal("62", protocol.GetProperty("source").GetProperty("setCommand").GetString());
    Equal("D5", protocol.GetProperty("eq").GetProperty("presetQueryCommand").GetString());
    Equal("C4", protocol.GetProperty("eq").GetProperty("presetSetCommand").GetString());
    Equal("43", protocol.GetProperty("eq").GetProperty("customQueryCommand").GetString());
    Equal("44", protocol.GetProperty("eq").GetProperty("singleBandGainSetCommand").GetString());
    Equal("66", protocol.GetProperty("volume").GetProperty("queryCommand").GetString());
    Equal("67", protocol.GetProperty("volume").GetProperty("setCommand").GetString());

    var safety = root.GetProperty("safety");
    True(safety.GetProperty("identityRequiredBeforeControl").GetBoolean());
    True(safety.GetProperty("configuredPublicAddressRequired").GetBoolean());
    False(safety.GetProperty("unsupportedWritesEnabled").GetBoolean());

    var serialized = root.GetRawText();
    False(serialized.Contains("bindingEvidence", StringComparison.OrdinalIgnoreCase));
    False(serialized.Contains("evidenceRefs", StringComparison.OrdinalIgnoreCase));
    False(serialized.Contains("verifiedOn", StringComparison.OrdinalIgnoreCase));
    False(serialized.Contains("docs/evidence", StringComparison.OrdinalIgnoreCase));
    False(serialized.Contains("work/", StringComparison.OrdinalIgnoreCase));
}
static byte[] CustomEqFixture(byte band3Gain = 6)
{
    var payload = Convert.FromHexString(
        "0306" +
        "0000003E0607" +
        "010000FA0607" +
        "020003E80607" +
        "03000FA00607" +
        "04001F400607" +
        "05003E800607" +
        "68554E6A536F756E642065666665637473");
    payload[18] = band3Gain;
    return payload;
}

static ScriptedExchange CustomEqPreflightExchange(byte[] payload)
{
    var exchange = new ScriptedExchange();
    exchange.OnWrite = frame =>
    {
        var hex = Convert.ToHexString(frame);
        if (hex == "AAECC900005F") exchange.Push(Incoming(0xC9, Encoding.ASCII.GetBytes("EDIFIER S880 MKII")));
        if (hex == "AAECD500006B") exchange.Push(Incoming(0xD5, [0x00]));
        if (hex == "AAEC430000D9") exchange.Push(Incoming(0x43, payload));
        return Task.CompletedTask;
    };
    return exchange;
}

static byte[] Incoming(byte command, byte[] payload)
{
    var frame = EdifierS880Mk2CnProtocol.Build(command, payload);
    frame[0] = 0xBB;
    frame[^1] = EdifierS880Mk2CnProtocol.Checksum(frame.AsSpan(0, frame.Length - 1));
    return frame;
}

static byte[] Concat(params byte[][] values) => values.SelectMany(value => value).ToArray();

static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}");
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}");
}

static T AssertType<T>(object? value)
{
    if (value is T typed) return typed;
    throw new InvalidOperationException($"Expected {typeof(T).Name}, got {value?.GetType().Name ?? "null"}");
}

static void True(bool value)
{
    if (!value) throw new InvalidOperationException("Expected true");
}

static void False(bool value)
{
    if (value) throw new InvalidOperationException("Expected false");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'");
}

static void Contains(string expected, string? actual)
{
    if (actual is null || !actual.Contains(expected, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException($"Expected '{actual}' to contain '{expected}'");
}

internal sealed class CaptureConsole : ICommandConsole
{
    private readonly List<string> _output = [];
    private readonly List<string> _errors = [];
    public string ErrorText => string.Join(Environment.NewLine, _errors);
    public void WriteLine(string value) => _output.Add(value);
    public void WriteError(string value) => _errors.Add(value);
}

internal sealed class FakeBluetoothCommands : IBluetoothCommands
{
    public bool AdapterCalled { get; private set; }
    public bool SourceCalled { get; private set; }
    public bool EqCalled { get; private set; }
    public bool VolumeCalled { get; private set; }

    public Task<int> AdapterAsync(ICommandConsole console, CancellationToken cancellationToken)
    {
        AdapterCalled = true;
        return Task.FromResult(77);
    }

    public Task<int> DiscoverAsync(DiscoverCommand command, ICommandConsole console, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Unexpected discovery call");

    public Task<int> GattAsync(GattCommand command, ICommandConsole console, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Unexpected GATT call");

    public Task<int> SourceAsync(SourceCommand command, ICommandConsole console, CancellationToken cancellationToken)
    {
        SourceCalled = true;
        return Task.FromResult(71);
    }

    public Task<int> EqAsync(EqCommand command, ICommandConsole console, CancellationToken cancellationToken)
    {
        EqCalled = true;
        return Task.FromResult(72);
    }

    public Task<int> VolumeAsync(VolumeCommand command, ICommandConsole console, CancellationToken cancellationToken)
    {
        VolumeCalled = true;
        return Task.FromResult(73);
    }
}

internal sealed record FakeBleDevice(string Name);

internal sealed class FakeBleTransport : IBleResolutionTransport<FakeBleDevice>
{
    public Func<int, Task<FakeBleDevice?>> ResolveResult { get; set; } = _ => Task.FromResult<FakeBleDevice?>(null);
    public BleAdapterReadiness Readiness { get; set; } = BleAdapterReadiness.Available("on");
    public FakeAdvertisementSession Session { get; } = new();
    public TaskCompletionSource FirstResolveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int ResolveCalls { get; private set; }
    public int AdapterCalls { get; private set; }
    public int CreateSessionCalls { get; private set; }

    public Task<FakeBleDevice?> ResolveAsync(
        ulong address,
        BluetoothAddressKind addressType,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ResolveCalls++;
        FirstResolveStarted.TrySetResult();
        return ResolveResult(ResolveCalls);
    }

    public Task<BleAdapterReadiness> GetAdapterReadinessAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AdapterCalls++;
        return Task.FromResult(Readiness);
    }

    public IBleAdvertisementSession CreateAdvertisementSession()
    {
        CreateSessionCalls++;
        return Session;
    }
}

internal sealed class FakeAdvertisementSession : IBleAdvertisementSession
{
    private Action<BleAdvertisement>? _received;
    private Action<BleDiscoveryStopped>? _stopped;

    public event Action<BleAdvertisement>? AdvertisementReceived
    {
        add => _received += value;
        remove => _received -= value;
    }

    public event Action<BleDiscoveryStopped>? Stopped
    {
        add => _stopped += value;
        remove => _stopped -= value;
    }

    public Action<FakeAdvertisementSession>? OnStart { get; set; }
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool StopCalled { get; private set; }
    public bool Disposed { get; private set; }
    public int ReceivedSubscriberCount => _received?.GetInvocationList().Length ?? 0;
    public int StoppedSubscriberCount => _stopped?.GetInvocationList().Length ?? 0;

    public void Start()
    {
        Started.TrySetResult();
        OnStart?.Invoke(this);
    }

    public void Stop()
    {
        StopCalled = true;
        _stopped?.Invoke(new("Success", null));
    }

    public void Dispose() => Disposed = true;

    public void Emit(BleAdvertisement advertisement) => _received?.Invoke(advertisement);
}

internal sealed class ScriptedExchange : IEcExchange
{
    private readonly NotificationInbox _inbox = new();
    private readonly List<string> _writes = [];
    public Func<byte[], Task>? OnWrite { get; set; }
    public IReadOnlyList<string> WritesHex => _writes;
    public bool SetAttempted { get; private set; }

    public EcFrameWaiter RegisterWaiter(Func<EcFrame, bool> predicate) => _inbox.Register(predicate);

    public async Task WriteAsync(byte[] frame, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _writes.Add(Convert.ToHexString(frame));
        if (frame[2] is 0x62 or 0xC4 or 0x44 or 0x67) SetAttempted = true;
        if (OnWrite is not null) await OnWrite(frame);
    }

    public void Push(byte[] bytes) => _inbox.Push(bytes);
}
