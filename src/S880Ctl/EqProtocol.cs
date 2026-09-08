using System.Text;

namespace S880Ctl;

internal static class EdifierS880Mk2CnEqProtocol
{
    public static byte[] PresetQuery => EdifierS880Mk2CnProtocol.Build(0xD5, []);
    public static byte[] CustomQuery => EdifierS880Mk2CnProtocol.Build(0x43, []);
    public static byte[] PresetSet(EqPreset preset) => EdifierS880Mk2CnProtocol.Build(0xC4, [PresetCode(preset)]);

    public static byte[] Query(EqOperation operation) => operation switch
    {
        EqOperation.Get => PresetQuery,
        EqOperation.CustomGet => CustomQuery,
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    public static byte ResponseCommand(EqOperation operation) => operation switch
    {
        EqOperation.Get => 0xD5,
        EqOperation.CustomGet => 0x43,
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    public static string OperationName(EqOperation operation) => operation switch
    {
        EqOperation.Get => "get",
        EqOperation.CustomGet => "custom-get",
        EqOperation.Set => "set",
        EqOperation.CustomSet => "custom-set",
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    public static byte PresetCode(EqPreset preset) => preset switch
    {
        EqPreset.Classic => 0x00,
        EqPreset.Monitor => 0x01,
        EqPreset.Dynamic => 0x02,
        EqPreset.Vocal => 0x03,
        EqPreset.Custom => 0x04,
        _ => throw new ArgumentOutOfRangeException(nameof(preset))
    };

    public static string? PresetName(byte code) => code switch
    {
        0x00 => "classic",
        0x01 => "monitor",
        0x02 => "dynamic",
        0x03 => "vocal",
        0x04 => "custom",
        _ => null
    };
}

internal static class EcEqController
{
    public static async Task<EqProtocolOutcome> ExecuteAsync(
        EqOperation operation,
        IEcExchange exchange,
        CancellationToken cancellationToken,
        EqPreset? targetPreset = null)
    {
        if (operation == EqOperation.Set && targetPreset is null)
            throw new ArgumentException("EQ set requires a target preset", nameof(targetPreset));

        var (productName, identity) = await VerifyIdentityAsync(exchange, cancellationToken);

        if (operation == EqOperation.Set)
            return await ExecuteSetAsync(productName, identity, targetPreset!.Value, exchange, cancellationToken);

        var responseCommand = EdifierS880Mk2CnEqProtocol.ResponseCommand(operation);
        using var queryWaiter = exchange.RegisterWaiter(frame => frame.Command == responseCommand);
        var query = EdifierS880Mk2CnEqProtocol.Query(operation);
        await exchange.WriteAsync(query, cancellationToken);
        var response = await queryWaiter.WaitAsync(cancellationToken);

        byte? presetCode = null;
        string? presetName = null;
        byte? customFormatByte = null;
        CustomEqSnapshot? customSnapshot = null;
        var semanticDecoded = false;
        string interpretation;

        if (operation == EqOperation.Get)
        {
            if (response.Payload.Length == 1 &&
                EdifierS880Mk2CnEqProtocol.PresetName(response.Payload[0]) is { } knownPreset)
            {
                presetCode = response.Payload[0];
                presetName = knownPreset;
                semanticDecoded = true;
                interpretation = "single-byte-known-preset";
            }
            else
            {
                interpretation = response.Payload.Length == 1
                    ? "single-byte-unknown-preset"
                    : "unexpected-preset-payload-preserved-without-calibration-inference";
            }
        }
        else
        {
            customFormatByte = response.Payload.Length > 0 ? response.Payload[0] : null;
            if (EdifierS880Mk2CnCustomEqProtocol.TryParse(response.Payload, out customSnapshot, out _))
            {
                semanticDecoded = true;
                interpretation = "validated-format-03-six-band-custom-eq";
            }
            else
            {
                interpretation = customFormatByte.HasValue
                    ? "raw-custom-eq-payload-with-uninterpreted-first-format-byte"
                    : "empty-custom-eq-payload";
            }
        }

        return new(
            productName,
            identity.RawHex,
            EdifierS880Mk2CnEqProtocol.OperationName(operation),
            Convert.ToHexString(query),
            response.RawHex,
            Convert.ToHexString(response.Payload),
            presetCode,
            presetName,
            customFormatByte,
            customSnapshot,
            semanticDecoded,
            interpretation,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            "query-response",
            exchange.WritesHex.ToArray());
    }

    private static async Task<EqProtocolOutcome> ExecuteSetAsync(
        string productName,
        EcFrame identity,
        EqPreset targetPreset,
        IEcExchange exchange,
        CancellationToken cancellationToken)
    {
        using var currentWaiter = exchange.RegisterWaiter(frame => frame.Command == 0xD5);
        var query = EdifierS880Mk2CnEqProtocol.PresetQuery;
        await exchange.WriteAsync(query, cancellationToken);
        var current = await currentWaiter.WaitAsync(cancellationToken);
        var (currentCode, currentName) = RequireKnownPreset(current, "initial EQ query");
        var targetCode = EdifierS880Mk2CnEqProtocol.PresetCode(targetPreset);
        var targetName = EdifierS880Mk2CnEqProtocol.PresetName(targetCode)!;

        if (currentCode == targetCode)
        {
            return new(
                productName,
                identity.RawHex,
                "set",
                Convert.ToHexString(query),
                current.RawHex,
                Convert.ToHexString(current.Payload),
                currentCode,
                currentName,
                null,
                null,
                true,
                "single-byte-known-preset",
                currentCode,
                currentName,
                targetCode,
                targetName,
                null,
                null,
                null,
                null,
                currentCode,
                currentName,
                current.RawHex,
                current.RawHex,
                "already-current",
                exchange.WritesHex.ToArray());
        }

        using var ackWaiter = exchange.RegisterWaiter(frame => frame.Command == 0xC4);
        var setFrame = EdifierS880Mk2CnEqProtocol.PresetSet(targetPreset);
        await exchange.WriteAsync(setFrame, cancellationToken);
        var ack = await ackWaiter.WaitAsync(cancellationToken);
        if (ack.Payload.Length != 1)
            throw new EqProtocolException($"EQ preset-set response was not a single-byte acknowledgement: {ack.RawHex}");
        var ackPayloadByte = ack.Payload[0];

        using var verifyWaiter = exchange.RegisterWaiter(frame => frame.Command == 0xD5);
        await exchange.WriteAsync(EdifierS880Mk2CnEqProtocol.PresetQuery, cancellationToken);
        var final = await verifyWaiter.WaitAsync(cancellationToken);
        var (finalCode, finalName) = RequireKnownPreset(final, "EQ readback");
        if (finalCode != targetCode)
            throw new EqProtocolException($"EQ readback was 0x{finalCode:X2} ({finalName}), expected 0x{targetCode:X2} ({targetName})");

        return new(
            productName,
            identity.RawHex,
            "set",
            Convert.ToHexString(query),
            final.RawHex,
            Convert.ToHexString(final.Payload),
            finalCode,
            finalName,
            null,
            null,
            true,
            "single-byte-known-preset",
            currentCode,
            currentName,
            targetCode,
            targetName,
            Convert.ToHexString(setFrame),
            ack.RawHex,
            ackPayloadByte,
            "uninterpreted-single-byte; explicit D5 readback is authoritative",
            finalCode,
            finalName,
            current.RawHex,
            final.RawHex,
            "explicit-preset-query-after-ack",
            exchange.WritesHex.ToArray());
    }

    internal static async Task<(string ProductName, EcFrame Frame)> VerifyIdentityAsync(
        IEcExchange exchange,
        CancellationToken cancellationToken)
    {
        using var identityWaiter = exchange.RegisterWaiter(frame => frame.Command == 0xC9);
        await exchange.WriteAsync(EdifierS880Mk2CnProtocol.ProductIdentityQuery, cancellationToken);
        var identity = await identityWaiter.WaitAsync(cancellationToken);
        var productName = Encoding.ASCII.GetString(identity.Payload);
        if (!productName.Equals(EdifierS880Mk2CnProtocol.ExpectedProductName, StringComparison.Ordinal))
            throw new EqProtocolException($"product identity mismatch: '{productName}'");
        return (productName, identity);
    }

    internal static (byte Code, string Name) RequireKnownPreset(EcFrame frame, string context)
    {
        if (frame.Payload.Length != 1)
            throw new EqProtocolException($"{context} returned an unexpected payload; refusing to infer calibration bytes: {frame.RawHex}");
        var name = EdifierS880Mk2CnEqProtocol.PresetName(frame.Payload[0]);
        if (name is null)
            throw new EqProtocolException($"{context} returned unknown preset 0x{frame.Payload[0]:X2}: {frame.RawHex}");
        return (frame.Payload[0], name);
    }
}

internal sealed class EqProtocolException(string message) : Exception(message);

internal sealed record EqProtocolOutcome(
    string ProductName,
    string IdentityFrameHex,
    string Operation,
    string QueryFrameHex,
    string ResponseFrameHex,
    string RawPayloadHex,
    byte? PresetCode,
    string? PresetName,
    byte? CustomFormatByte,
    CustomEqSnapshot? CustomSnapshot,
    bool SemanticDecoded,
    string Interpretation,
    byte? CurrentBeforeCode,
    string? CurrentBeforeName,
    byte? RequestedPresetCode,
    string? RequestedPresetName,
    string? SetFrameHex,
    string? AckFrameHex,
    byte? AckPayloadByte,
    string? AckInterpretation,
    byte? CurrentAfterCode,
    string? CurrentAfterName,
    string? CurrentBeforeFrameHex,
    string? CurrentAfterFrameHex,
    string Verification,
    IReadOnlyList<string> WritesHex);
