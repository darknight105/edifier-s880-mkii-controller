using System.Globalization;
using System.Text.Json;

namespace S880Ctl;

public static class ExitCodes
{
    public const int Success = 0;
    public const int UnexpectedError = 1;
    public const int UsageError = 2;
    public const int ProtocolNotVerified = 3;
    public const int AdapterUnavailable = 10;
    public const int BluetoothError = 11;
    public const int Timeout = 12;
    public const int OutputError = 20;
    public const int Cancelled = 130;
}

public interface ICommandConsole
{
    void WriteLine(string value);
    void WriteError(string value);
}

internal sealed class SystemConsole : ICommandConsole
{
    public void WriteLine(string value) => Console.Out.WriteLine(value);
    public void WriteError(string value) => Console.Error.WriteLine(value);
}

public abstract record CliCommand;
public sealed record HelpCommand : CliCommand;
public sealed record AdapterCommand : CliCommand;
public sealed record DiscoverCommand(int Seconds, bool Active, string OutputPath) : CliCommand;
public sealed record GattCommand(ulong Address, string AddressText, BluetoothAddressKind AddressType, int TimeoutSeconds, string OutputPath) : CliCommand;
public sealed record SourceCommand(SourceOperation Operation, ulong Address, string AddressText, BluetoothAddressKind AddressType, int TimeoutSeconds, string OutputPath) : CliCommand;
public sealed record EqCommand(EqOperation Operation, EqPreset? Preset, int? Band, decimal? GainDb, ulong Address, string AddressText, BluetoothAddressKind AddressType, int TimeoutSeconds, string OutputPath) : CliCommand;
public sealed record VolumeCommand(VolumeOperation Operation, int? Value, ulong Address, string AddressText, BluetoothAddressKind AddressType, int TimeoutSeconds, string OutputPath) : CliCommand;
public enum SourceOperation { Get, Usb, LineIn1, LineIn2, Bluetooth, Optical, Coaxial }
public enum EqOperation { Get, CustomGet, Set, CustomSet }
public enum EqPreset { Classic, Monitor, Dynamic, Vocal, Custom }
public enum VolumeOperation { Get, Set }
public enum BluetoothAddressKind { Public, Random }
public sealed record CliParseResult(CliCommand? Command, string? Error)
{
    public bool IsSuccess => Command is not null && Error is null;
}

public static class CliParser
{
    private static readonly HashSet<string> Sources = new(StringComparer.OrdinalIgnoreCase)
    {
        "usb", "bluetooth", "optical", "coaxial", "line-in-1", "line-in-2"
    };

    public static CliParseResult Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || IsHelp(args[0]))
        {
            return new(new HelpCommand(), null);
        }

        return args[0].ToLowerInvariant() switch
        {
            "adapter" => args.Count == 1
                ? new(new AdapterCommand(), null)
                : Error("adapter does not accept options"),
            "discover" => ParseDiscover(args),
            "gatt" => ParseGatt(args),
            "source" => ParseSource(args),
            "eq" => ParseEq(args),
            "volume" => ParseVolume(args),
            _ => Error($"unknown command: {args[0]}")
        };
    }

    private static CliParseResult ParseDiscover(IReadOnlyList<string> args)
    {
        var seconds = 20;
        var active = false;
        string? output = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 1; index < args.Count; index++)
        {
            var option = args[index];
            if (!seen.Add(option))
            {
                return Error($"duplicate option: {option}");
            }

            switch (option.ToLowerInvariant())
            {
                case "--active":
                    active = true;
                    break;
                case "--seconds":
                    if (!TryTake(args, ref index, out var secondsText) ||
                        !int.TryParse(secondsText, NumberStyles.None, CultureInfo.InvariantCulture, out seconds) ||
                        seconds is < 1 or > 300)
                    {
                        return Error("--seconds must be an integer from 1 to 300");
                    }
                    break;
                case "--output":
                    if (!TryTake(args, ref index, out output) || string.IsNullOrWhiteSpace(output))
                    {
                        return Error("--output requires a path");
                    }
                    break;
                default:
                    return Error($"unknown discover option: {option}");
            }
        }

        return output is null
            ? Error("discover requires --output PATH")
            : new(new DiscoverCommand(seconds, active, output), null);
    }

    private static CliParseResult ParseGatt(IReadOnlyList<string> args)
    {
        string? addressText = null;
        string? addressTypeText = null;
        string? output = null;
        var timeoutSeconds = 30;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 1; index < args.Count; index++)
        {
            var option = args[index];
            if (!seen.Add(option))
            {
                return Error($"duplicate option: {option}");
            }

            switch (option.ToLowerInvariant())
            {
                case "--address":
                    if (!TryTake(args, ref index, out addressText)) return Error("--address requires a value");
                    break;
                case "--address-type":
                    if (!TryTake(args, ref index, out addressTypeText)) return Error("--address-type requires public or random");
                    break;
                case "--output":
                    if (!TryTake(args, ref index, out output) || string.IsNullOrWhiteSpace(output)) return Error("--output requires a path");
                    break;
                case "--timeout-seconds":
                    if (!TryTake(args, ref index, out var timeoutText) ||
                        !int.TryParse(timeoutText, NumberStyles.None, CultureInfo.InvariantCulture, out timeoutSeconds) ||
                        timeoutSeconds is < 1 or > 180)
                    {
                        return Error("--timeout-seconds must be an integer from 1 to 180");
                    }
                    break;
                default:
                    return Error($"unknown gatt option: {option}");
            }
        }

        if (addressText is null || !BluetoothAddress.TryParse(addressText, out var address))
        {
            return Error("gatt requires a 48-bit --address, for example AA:BB:CC:DD:EE:FF");
        }

        var addressType = addressTypeText?.ToLowerInvariant() switch
        {
            "public" => BluetoothAddressKind.Public,
            "random" => BluetoothAddressKind.Random,
            _ => (BluetoothAddressKind?)null
        };

        if (addressType is null) return Error("gatt requires --address-type public|random");
        if (output is null) return Error("gatt requires --output PATH");

        return new(new GattCommand(address, BluetoothAddress.Format(address), addressType.Value, timeoutSeconds, output), null);
    }

    private static CliParseResult ParseSource(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || !Sources.Contains(args[1]) && !args[1].Equals("get", StringComparison.OrdinalIgnoreCase))
        {
            return Error("source requires one of: get, usb, bluetooth, optical, coaxial, line-in-1, line-in-2");
        }

        var operationText = args[1].ToLowerInvariant();

        string? addressText = null;
        string? addressTypeText = null;
        string? output = null;
        var timeoutSeconds = 30;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 2; index < args.Count; index++)
        {
            var option = args[index];
            if (!seen.Add(option)) return Error($"duplicate option: {option}");
            switch (option.ToLowerInvariant())
            {
                case "--address":
                    if (!TryTake(args, ref index, out addressText)) return Error("--address requires a value");
                    break;
                case "--address-type":
                    if (!TryTake(args, ref index, out addressTypeText)) return Error("--address-type requires public or random");
                    break;
                case "--output":
                    if (!TryTake(args, ref index, out output) || string.IsNullOrWhiteSpace(output)) return Error("--output requires a path");
                    break;
                case "--timeout-seconds":
                    if (!TryTake(args, ref index, out var timeoutText) ||
                        !int.TryParse(timeoutText, NumberStyles.None, CultureInfo.InvariantCulture, out timeoutSeconds) ||
                        timeoutSeconds is < 1 or > 60)
                    {
                        return Error("source --timeout-seconds must be an integer from 1 to 60");
                    }
                    break;
                default:
                    return Error($"unknown source option: {option}");
            }
        }

        if (addressText is null || !BluetoothAddress.TryParse(addressText, out var address))
            return Error("source requires a 48-bit --address, for example AA:BB:CC:DD:EE:FF");

        var addressType = addressTypeText?.ToLowerInvariant() switch
        {
            "public" => BluetoothAddressKind.Public,
            "random" => BluetoothAddressKind.Random,
            _ => (BluetoothAddressKind?)null
        };
        if (addressType is null) return Error("source requires --address-type public|random");
        if (output is null) return Error("source requires --output PATH");

        var operation = operationText switch
        {
            "get" => SourceOperation.Get,
            "usb" => SourceOperation.Usb,
            "line-in-1" => SourceOperation.LineIn1,
            "line-in-2" => SourceOperation.LineIn2,
            "bluetooth" => SourceOperation.Bluetooth,
            "optical" => SourceOperation.Optical,
            "coaxial" => SourceOperation.Coaxial,
            _ => throw new InvalidOperationException("Unsupported source parser state")
        };
        return new(new SourceCommand(operation, address, BluetoothAddress.Format(address), addressType.Value, timeoutSeconds, output), null);
    }

    private static CliParseResult ParseEq(IReadOnlyList<string> args)
    {
        if (args.Count < 2)
            return Error("eq requires one of: get, custom-get, set PRESET, custom-set --band N --gain-db DB");

        var operation = args[1].ToLowerInvariant() switch
        {
            "get" => EqOperation.Get,
            "custom-get" => EqOperation.CustomGet,
            "set" => EqOperation.Set,
            "custom-set" => EqOperation.CustomSet,
            _ => (EqOperation?)null
        };
        if (operation is null)
            return Error("eq requires one of: get, custom-get, set PRESET, custom-set --band N --gain-db DB");

        EqPreset? preset = null;
        int? band = null;
        decimal? gainDb = null;
        var optionStart = 2;
        if (operation == EqOperation.Set)
        {
            if (args.Count < 3)
                return Error("eq set requires one of: classic, monitor, dynamic, vocal, custom");
            preset = args[2].ToLowerInvariant() switch
            {
                "classic" => EqPreset.Classic,
                "monitor" => EqPreset.Monitor,
                "dynamic" => EqPreset.Dynamic,
                "vocal" => EqPreset.Vocal,
                "custom" => EqPreset.Custom,
                _ => null
            };
            if (preset is null)
                return Error("eq set requires one of: classic, monitor, dynamic, vocal, custom");
            optionStart = 3;
        }

        string? addressText = null;
        string? addressTypeText = null;
        string? output = null;
        var timeoutSeconds = 30;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = optionStart; index < args.Count; index++)
        {
            var option = args[index];
            if (!seen.Add(option)) return Error($"duplicate option: {option}");
            switch (option.ToLowerInvariant())
            {
                case "--address":
                    if (!TryTake(args, ref index, out addressText)) return Error("--address requires a value");
                    break;
                case "--address-type":
                    if (!TryTake(args, ref index, out addressTypeText)) return Error("--address-type requires public or random");
                    break;
                case "--output":
                    if (!TryTake(args, ref index, out output) || string.IsNullOrWhiteSpace(output)) return Error("--output requires a path");
                    break;
                case "--timeout-seconds":
                    if (!TryTake(args, ref index, out var timeoutText) ||
                        !int.TryParse(timeoutText, NumberStyles.None, CultureInfo.InvariantCulture, out timeoutSeconds) ||
                        timeoutSeconds is < 1 or > 60)
                    {
                        return Error("eq --timeout-seconds must be an integer from 1 to 60");
                    }
                    break;
                case "--band" when operation == EqOperation.CustomSet:
                    if (!TryTake(args, ref index, out var bandText) ||
                        !int.TryParse(bandText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedBand) ||
                        parsedBand is < 1 or > 6)
                    {
                        return Error("eq custom-set --band must be an integer from 1 to 6");
                    }
                    band = parsedBand;
                    break;
                case "--gain-db" when operation == EqOperation.CustomSet:
                    if (!TryTake(args, ref index, out var gainText) ||
                        !decimal.TryParse(gainText, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsedGain) ||
                        parsedGain is < -3m or > 3m || parsedGain * 2m != decimal.Truncate(parsedGain * 2m))
                    {
                        return Error("eq custom-set --gain-db must be from -3 to 3 in exact 0.5 dB steps");
                    }
                    gainDb = parsedGain;
                    break;
                default:
                    return Error($"unknown eq option: {option}");
            }
        }

        if (addressText is null || !BluetoothAddress.TryParse(addressText, out var address))
            return Error("eq requires a 48-bit --address, for example AA:BB:CC:DD:EE:FF");

        var addressType = addressTypeText?.ToLowerInvariant() switch
        {
            "public" => BluetoothAddressKind.Public,
            "random" => BluetoothAddressKind.Random,
            _ => (BluetoothAddressKind?)null
        };
        if (addressType is null) return Error("eq requires --address-type public|random");
        if (output is null) return Error("eq requires --output PATH");
        if (operation == EqOperation.CustomSet && (band is null || gainDb is null))
            return Error("eq custom-set requires --band 1..6 and --gain-db -3..3 in 0.5 dB steps");

        return new(new EqCommand(operation.Value, preset, band, gainDb, address, BluetoothAddress.Format(address), addressType.Value, timeoutSeconds, output), null);
    }

    private static CliParseResult ParseVolume(IReadOnlyList<string> args)
    {
        if (args.Count < 2)
            return Error("volume requires one of: get, set VALUE");

        var operation = args[1].ToLowerInvariant() switch
        {
            "get" => VolumeOperation.Get,
            "set" => VolumeOperation.Set,
            _ => (VolumeOperation?)null
        };
        if (operation is null)
            return Error("volume requires one of: get, set VALUE");

        int? value = null;
        var optionStart = 2;
        if (operation == VolumeOperation.Set)
        {
            if (args.Count < 3 ||
                !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedValue) ||
                parsedValue is < 0 or > EdifierS880Mk2CnVolumeProtocol.ExpectedMaximum)
            {
                return Error($"volume set requires an integer from 0 to {EdifierS880Mk2CnVolumeProtocol.ExpectedMaximum}");
            }
            value = parsedValue;
            optionStart = 3;
        }

        string? addressText = null;
        string? addressTypeText = null;
        string? output = null;
        var timeoutSeconds = 30;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = optionStart; index < args.Count; index++)
        {
            var option = args[index];
            if (!seen.Add(option)) return Error($"duplicate option: {option}");
            switch (option.ToLowerInvariant())
            {
                case "--address":
                    if (!TryTake(args, ref index, out addressText)) return Error("--address requires a value");
                    break;
                case "--address-type":
                    if (!TryTake(args, ref index, out addressTypeText)) return Error("--address-type requires public or random");
                    break;
                case "--output":
                    if (!TryTake(args, ref index, out output) || string.IsNullOrWhiteSpace(output)) return Error("--output requires a path");
                    break;
                case "--timeout-seconds":
                    if (!TryTake(args, ref index, out var timeoutText) ||
                        !int.TryParse(timeoutText, NumberStyles.None, CultureInfo.InvariantCulture, out timeoutSeconds) ||
                        timeoutSeconds is < 1 or > 60)
                    {
                        return Error("volume --timeout-seconds must be an integer from 1 to 60");
                    }
                    break;
                default:
                    return Error($"unknown volume option: {option}");
            }
        }

        if (addressText is null || !BluetoothAddress.TryParse(addressText, out var address))
            return Error("volume requires a 48-bit --address, for example AA:BB:CC:DD:EE:FF");
        var addressType = addressTypeText?.ToLowerInvariant() switch
        {
            "public" => BluetoothAddressKind.Public,
            "random" => BluetoothAddressKind.Random,
            _ => (BluetoothAddressKind?)null
        };
        if (addressType is null) return Error("volume requires --address-type public|random");
        if (output is null) return Error("volume requires --output PATH");

        return new(new VolumeCommand(operation.Value, value, address, BluetoothAddress.Format(address), addressType.Value, timeoutSeconds, output), null);
    }

    private static bool TryTake(IReadOnlyList<string> args, ref int index, out string? value)
    {
        if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = null;
            return false;
        }

        value = args[++index];
        return true;
    }

    private static bool IsHelp(string value) => value is "-h" or "--help" or "help";
    private static CliParseResult Error(string message) => new(null, message);
}

public static class BluetoothAddress
{
    public static bool TryParse(string text, out ulong value)
    {
        value = 0;
        var normalized = text.Replace(":", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        return normalized.Length == 12 && ulong.TryParse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    public static string Format(ulong value) => string.Join(":", Enumerable.Range(0, 6)
        .Select(index => ((value >> ((5 - index) * 8)) & 0xff).ToString("X2", CultureInfo.InvariantCulture)));
}

public interface IBluetoothCommands
{
    Task<int> AdapterAsync(ICommandConsole console, CancellationToken cancellationToken);
    Task<int> DiscoverAsync(DiscoverCommand command, ICommandConsole console, CancellationToken cancellationToken);
    Task<int> GattAsync(GattCommand command, ICommandConsole console, CancellationToken cancellationToken);
    Task<int> SourceAsync(SourceCommand command, ICommandConsole console, CancellationToken cancellationToken);
    Task<int> EqAsync(EqCommand command, ICommandConsole console, CancellationToken cancellationToken);
    Task<int> VolumeAsync(VolumeCommand command, ICommandConsole console, CancellationToken cancellationToken);
}

public static class S880Application
{
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        ICommandConsole console,
        Func<IBluetoothCommands> bluetoothFactory,
        CancellationToken cancellationToken) =>
        await RunAsync(args, console, bluetoothFactory, cancellationToken, SpeakerTargetBinding.Current);

    internal static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        ICommandConsole console,
        Func<IBluetoothCommands> bluetoothFactory,
        CancellationToken cancellationToken,
        SpeakerTargetBinding targetBinding)
    {
        var parsed = CliParser.Parse(args);
        if (!parsed.IsSuccess)
        {
            console.WriteError(StatusJson("parse", "error", ExitCodes.UsageError, parsed.Error));
            console.WriteError(Usage);
            return ExitCodes.UsageError;
        }

        try
        {
            return parsed.Command switch
            {
                HelpCommand => ShowHelp(console),
                SourceCommand source => await DispatchSourceAsync(source, console, bluetoothFactory, cancellationToken, targetBinding),
                EqCommand eq => await DispatchEqAsync(eq, console, bluetoothFactory, cancellationToken, targetBinding),
                VolumeCommand volume => await DispatchVolumeAsync(volume, console, bluetoothFactory, cancellationToken, targetBinding),
                AdapterCommand => await bluetoothFactory().AdapterAsync(console, cancellationToken),
                DiscoverCommand discover => await bluetoothFactory().DiscoverAsync(discover, console, cancellationToken),
                GattCommand gatt => await bluetoothFactory().GattAsync(gatt, console, cancellationToken),
                _ => ExitCodes.UnexpectedError
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            console.WriteError(StatusJson("command", "cancelled", ExitCodes.Cancelled, "cancelled"));
            return ExitCodes.Cancelled;
        }
        catch (Exception error)
        {
            console.WriteError(StatusJson("command", "error", ExitCodes.UnexpectedError, error.Message));
            return ExitCodes.UnexpectedError;
        }
    }

    public static string StatusJson(string command, string status, int exitCode, string? error = null, string? output = null) =>
        JsonSerializer.Serialize(new { command, status, exitCode, error, output }, JsonDefaults.Options);

    private static int ShowHelp(ICommandConsole console)
    {
        console.WriteLine(Usage);
        return ExitCodes.Success;
    }

    private static async Task<int> DispatchSourceAsync(
        SourceCommand command,
        ICommandConsole console,
        Func<IBluetoothCommands> bluetoothFactory,
        CancellationToken cancellationToken,
        SpeakerTargetBinding targetBinding)
    {
        var bindingError = BindingError(command.Address, command.AddressText, command.AddressType, targetBinding);
        if (bindingError is not null)
        {
            console.WriteError(StatusJson(
                "source",
                "blocked",
                ExitCodes.ProtocolNotVerified,
                bindingError,
                command.OutputPath));
            return ExitCodes.ProtocolNotVerified;
        }

        return await bluetoothFactory().SourceAsync(command, console, cancellationToken);
    }

    private static async Task<int> DispatchEqAsync(
        EqCommand command,
        ICommandConsole console,
        Func<IBluetoothCommands> bluetoothFactory,
        CancellationToken cancellationToken,
        SpeakerTargetBinding targetBinding)
    {
        var bindingError = BindingError(command.Address, command.AddressText, command.AddressType, targetBinding);
        if (bindingError is not null)
        {
            console.WriteError(StatusJson(
                "eq",
                "blocked",
                ExitCodes.ProtocolNotVerified,
                bindingError,
                command.OutputPath));
            return ExitCodes.ProtocolNotVerified;
        }

        return await bluetoothFactory().EqAsync(command, console, cancellationToken);
    }

    private static async Task<int> DispatchVolumeAsync(
        VolumeCommand command,
        ICommandConsole console,
        Func<IBluetoothCommands> bluetoothFactory,
        CancellationToken cancellationToken,
        SpeakerTargetBinding targetBinding)
    {
        var bindingError = BindingError(command.Address, command.AddressText, command.AddressType, targetBinding);
        if (bindingError is not null)
        {
            console.WriteError(StatusJson(
                "volume",
                "blocked",
                ExitCodes.ProtocolNotVerified,
                bindingError,
                command.OutputPath));
            return ExitCodes.ProtocolNotVerified;
        }

        return await bluetoothFactory().VolumeAsync(command, console, cancellationToken);
    }

    private static string? BindingError(
        ulong address,
        string addressText,
        BluetoothAddressKind addressType,
        SpeakerTargetBinding targetBinding)
    {
        if (!targetBinding.IsConfigured)
            return "this build has no configured speaker address; rebuild with -p:SpeakerAddress=AA:BB:CC:DD:EE:FF; no Bluetooth access occurred";
        if (address == 0 || address != targetBinding.Address || addressType != BluetoothAddressKind.Public)
            return $"address {addressText} ({addressType.ToString().ToLowerInvariant()}) does not match this build's configured public speaker target; no Bluetooth access occurred";
        return null;
    }

    public const string Usage = """
        s880ctl - experimental EDIFIER S880 MKII CN control and query CLI

        Usage:
          s880ctl adapter
          s880ctl discover [--seconds 20] [--active] --output PATH
          s880ctl gatt --address AA:BB:CC:DD:EE:FF --address-type public|random --output PATH [--timeout-seconds 30]
          s880ctl source get|usb|line-in-1|line-in-2|bluetooth|optical|coaxial --address AA:BB:CC:DD:EE:FF --address-type public|random --output PATH [--timeout-seconds 30]
          s880ctl eq get|custom-get --address AA:BB:CC:DD:EE:FF --address-type public --output PATH [--timeout-seconds 30]
          s880ctl eq set classic|monitor|dynamic|vocal|custom --address AA:BB:CC:DD:EE:FF --address-type public --output PATH [--timeout-seconds 30]
          s880ctl eq custom-set --band 1..6 --gain-db -3..3 --address AA:BB:CC:DD:EE:FF --address-type public --output PATH [--timeout-seconds 30]
          s880ctl volume get|set VALUE --address AA:BB:CC:DD:EE:FF --address-type public --output PATH [--timeout-seconds 30]

        Safety:
          discover listens only; passive scanning is the default.
          gatt connects only to the exact address supplied and enumerates metadata without reads, writes, or subscriptions.
          source, eq, and volume require a build-time speaker address and verify the on-device product identity before access.
          all six source switches are real-device verified; audio quality was not tested for Line In 1/2, Optical, or Coaxial.
          eq set changes only the five identified presets after a strict D5 preflight and verifies the result with a second D5 query.
          eq custom-set changes only one gain byte after strict format-03 preflight and verifies the complete Custom EQ payload afterward.
          volume reads or changes the speaker's own command-66/67 level from 0 to 30; zero is not labeled as a separate mute feature.
        """;
}

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}
