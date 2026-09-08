namespace S880Ctl;

internal static class EdifierS880Mk2CnCustomEqProtocol
{
    private static readonly ushort[] ExpectedFrequencies = [62, 250, 1000, 4000, 8000, 16000];
    public const byte Format = 0x03;
    public const byte BandCount = 0x06;
    public const int HeaderLength = 2;
    public const int RecordLength = 6;
    public const int RecordsLength = BandCount * RecordLength;
    public const int MinimumPayloadLength = HeaderLength + RecordsLength;

    public static bool TryParse(ReadOnlySpan<byte> payload, out CustomEqSnapshot? snapshot, out string? error)
    {
        snapshot = null;
        if (payload.Length < MinimumPayloadLength)
        {
            error = $"Custom EQ payload was {payload.Length} bytes; format 03 requires at least {MinimumPayloadLength}";
            return false;
        }
        if (payload[0] != Format || payload[1] != BandCount)
        {
            error = $"Custom EQ format/count was {payload[0]:X2}/{payload[1]:X2}; required 03/06";
            return false;
        }

        var bands = new List<CustomEqBand>(BandCount);
        for (var index = 0; index < BandCount; index++)
        {
            var offset = HeaderLength + index * RecordLength;
            var recordIndex = payload[offset];
            var reserved = payload[offset + 1];
            var frequency = (ushort)((payload[offset + 2] << 8) | payload[offset + 3]);
            var gainRaw = payload[offset + 4];
            var qRaw = payload[offset + 5];
            if (recordIndex != index)
            {
                error = $"Custom EQ band record {index + 1} reported index {recordIndex}; required {index}";
                return false;
            }
            if (reserved != 0)
            {
                error = $"Custom EQ band {index + 1} reserved byte was 0x{reserved:X2}; required 00";
                return false;
            }
            if (frequency != ExpectedFrequencies[index])
            {
                error = $"Custom EQ band {index + 1} frequency was {frequency}; required {ExpectedFrequencies[index]}";
                return false;
            }
            if (gainRaw > 12)
            {
                error = $"Custom EQ band {index + 1} gain byte was {gainRaw}; required 0..12";
                return false;
            }

            bands.Add(new(index + 1, recordIndex, reserved, frequency, gainRaw, RawGainToDb(gainRaw), qRaw));
        }

        var rawPayload = payload.ToArray();
        snapshot = new(
            payload[0],
            payload[1],
            bands,
            rawPayload.AsSpan(MinimumPayloadLength).ToArray(),
            rawPayload);
        error = null;
        return true;
    }

    public static CustomEqSnapshot Parse(EcFrame frame, string context)
    {
        if (TryParse(frame.Payload, out var snapshot, out var error)) return snapshot!;
        throw new EqProtocolException($"{context} rejected: {error}; raw frame {frame.RawHex}");
    }

    public static byte GainDbToRaw(decimal gainDb)
    {
        if (gainDb is < -3m or > 3m || gainDb * 2m != decimal.Truncate(gainDb * 2m))
            throw new ArgumentOutOfRangeException(nameof(gainDb), "gain must be from -3 to 3 in exact 0.5 dB steps");
        return (byte)(gainDb * 2m + 6m);
    }

    public static decimal RawGainToDb(byte gainRaw)
    {
        if (gainRaw > 12) throw new ArgumentOutOfRangeException(nameof(gainRaw));
        return (gainRaw - 6) / 2m;
    }

    public static byte[] BandSet(CustomEqSnapshot snapshot, int band, decimal gainDb)
    {
        if (band is < 1 or > BandCount) throw new ArgumentOutOfRangeException(nameof(band));
        var gainRaw = GainDbToRaw(gainDb);
        var offset = HeaderLength + (band - 1) * RecordLength;
        var record = snapshot.RawPayload.AsSpan(offset, RecordLength).ToArray();
        record[4] = gainRaw;
        return EdifierS880Mk2CnProtocol.Build(0x44, record);
    }

    public static CustomEqSnapshot RequireOnlyTargetGainChanged(
        CustomEqSnapshot before,
        EcFrame afterFrame,
        int band,
        byte requestedGainRaw)
    {
        var after = Parse(afterFrame, "Custom EQ readback");
        var expected = before.RawPayload.ToArray();
        expected[HeaderLength + (band - 1) * RecordLength + 4] = requestedGainRaw;
        if (!after.RawPayload.AsSpan().SequenceEqual(expected))
        {
            throw new EqProtocolException(
                $"Custom EQ readback changed bytes outside the requested band gain or did not apply the gain; " +
                $"before={Convert.ToHexString(before.RawPayload)}, expected={Convert.ToHexString(expected)}, " +
                $"after={Convert.ToHexString(after.RawPayload)}");
        }
        return after;
    }
}

internal static class EcCustomEqGainController
{
    public static async Task<CustomEqGainOutcome> ExecuteAsync(
        int band,
        decimal gainDb,
        IEcExchange exchange,
        CancellationToken cancellationToken,
        CustomEqGainTrace? trace = null)
    {
        trace ??= new CustomEqGainTrace();
        trace.Band = band;
        trace.RequestedGainDb = gainDb;
        var (productName, identity) = await EcEqController.VerifyIdentityAsync(exchange, cancellationToken);
        trace.ProductName = productName;
        trace.IdentityFrameHex = identity.RawHex;

        using var presetBeforeWaiter = exchange.RegisterWaiter(frame => frame.Command == 0xD5);
        await exchange.WriteAsync(EdifierS880Mk2CnEqProtocol.PresetQuery, cancellationToken);
        var presetBeforeFrame = await presetBeforeWaiter.WaitAsync(cancellationToken);
        var (presetBeforeCode, presetBeforeName) = EcEqController.RequireKnownPreset(presetBeforeFrame, "initial EQ query");
        trace.PresetBeforeCode = presetBeforeCode;
        trace.PresetBeforeName = presetBeforeName;
        trace.PresetBeforeFrameHex = presetBeforeFrame.RawHex;

        using var customBeforeWaiter = exchange.RegisterWaiter(frame => frame.Command == 0x43);
        await exchange.WriteAsync(EdifierS880Mk2CnEqProtocol.CustomQuery, cancellationToken);
        var customBeforeFrame = await customBeforeWaiter.WaitAsync(cancellationToken);
        var before = EdifierS880Mk2CnCustomEqProtocol.Parse(customBeforeFrame, "Custom EQ preflight");
        trace.Before = before;
        trace.CustomBeforeFrameHex = customBeforeFrame.RawHex;
        if (band < 1 || band > before.Bands.Count)
            throw new EqProtocolException($"Custom EQ band {band} was outside the reported 1..{before.Bands.Count} range");

        var requestedGainRaw = EdifierS880Mk2CnCustomEqProtocol.GainDbToRaw(gainDb);
        trace.RequestedGainRaw = requestedGainRaw;
        var selectedBefore = before.Bands[band - 1];
        if (selectedBefore.GainRaw == requestedGainRaw)
        {
            return new(
                productName,
                identity.RawHex,
                band,
                selectedBefore.FrequencyHz,
                requestedGainRaw,
                gainDb,
                presetBeforeCode,
                presetBeforeName,
                presetBeforeFrame.RawHex,
                before,
                customBeforeFrame.RawHex,
                null,
                null,
                null,
                null,
                before,
                customBeforeFrame.RawHex,
                presetBeforeCode,
                presetBeforeName,
                presetBeforeFrame.RawHex,
                false,
                "already-current-gain",
                exchange.WritesHex.ToArray());
        }

        using var ackWaiter = exchange.RegisterWaiter(frame => frame.Command == 0x44);
        var setFrame = EdifierS880Mk2CnCustomEqProtocol.BandSet(before, band, gainDb);
        trace.SetFrameHex = Convert.ToHexString(setFrame);
        await exchange.WriteAsync(setFrame, cancellationToken);
        var ack = await ackWaiter.WaitAsync(cancellationToken);
        trace.AckFrameHex = ack.RawHex;
        if (ack.Payload.Length != 1)
            throw new EqProtocolException($"Custom EQ band-set response was not a single-byte acknowledgement: {ack.RawHex}");
        trace.AckPayloadByte = ack.Payload[0];

        using var customAfterWaiter = exchange.RegisterWaiter(frame => frame.Command == 0x43);
        await exchange.WriteAsync(EdifierS880Mk2CnEqProtocol.CustomQuery, cancellationToken);
        var customAfterFrame = await customAfterWaiter.WaitAsync(cancellationToken);
        var after = EdifierS880Mk2CnCustomEqProtocol.RequireOnlyTargetGainChanged(before, customAfterFrame, band, requestedGainRaw);
        trace.After = after;
        trace.CustomAfterFrameHex = customAfterFrame.RawHex;

        using var presetAfterWaiter = exchange.RegisterWaiter(frame => frame.Command == 0xD5);
        await exchange.WriteAsync(EdifierS880Mk2CnEqProtocol.PresetQuery, cancellationToken);
        var presetAfterFrame = await presetAfterWaiter.WaitAsync(cancellationToken);
        var (presetAfterCode, presetAfterName) = EcEqController.RequireKnownPreset(presetAfterFrame, "EQ query after Custom gain write");
        trace.PresetAfterCode = presetAfterCode;
        trace.PresetAfterName = presetAfterName;
        trace.PresetAfterFrameHex = presetAfterFrame.RawHex;

        return new(
            productName,
            identity.RawHex,
            band,
            selectedBefore.FrequencyHz,
            requestedGainRaw,
            gainDb,
            presetBeforeCode,
            presetBeforeName,
            presetBeforeFrame.RawHex,
            before,
            customBeforeFrame.RawHex,
            Convert.ToHexString(setFrame),
            ack.RawHex,
            ack.Payload[0],
            "uninterpreted-single-byte; complete 43 readback is authoritative",
            after,
            customAfterFrame.RawHex,
            presetAfterCode,
            presetAfterName,
            presetAfterFrame.RawHex,
            presetAfterCode != presetBeforeCode,
            "complete-custom-payload-readback-only-requested-gain-changed",
            exchange.WritesHex.ToArray());
    }
}

internal sealed record CustomEqBand(
    int Band,
    byte RecordIndex,
    byte Reserved,
    ushort FrequencyHz,
    byte GainRaw,
    decimal GainDb,
    byte QRaw);

internal sealed record CustomEqSnapshot(
    byte Format,
    byte Count,
    IReadOnlyList<CustomEqBand> Bands,
    byte[] OpaqueTail,
    byte[] RawPayload);

internal sealed record CustomEqGainOutcome(
    string ProductName,
    string IdentityFrameHex,
    int Band,
    ushort FrequencyHz,
    byte RequestedGainRaw,
    decimal RequestedGainDb,
    byte PresetBeforeCode,
    string PresetBeforeName,
    string PresetBeforeFrameHex,
    CustomEqSnapshot Before,
    string CustomBeforeFrameHex,
    string? SetFrameHex,
    string? AckFrameHex,
    byte? AckPayloadByte,
    string? AckInterpretation,
    CustomEqSnapshot After,
    string CustomAfterFrameHex,
    byte PresetAfterCode,
    string PresetAfterName,
    string PresetAfterFrameHex,
    bool FirmwareChangedPreset,
    string Verification,
    IReadOnlyList<string> WritesHex);

internal sealed class CustomEqGainTrace
{
    public string? ProductName { get; set; }
    public string? IdentityFrameHex { get; set; }
    public int Band { get; set; }
    public decimal RequestedGainDb { get; set; }
    public byte? RequestedGainRaw { get; set; }
    public byte? PresetBeforeCode { get; set; }
    public string? PresetBeforeName { get; set; }
    public string? PresetBeforeFrameHex { get; set; }
    public CustomEqSnapshot? Before { get; set; }
    public string? CustomBeforeFrameHex { get; set; }
    public string? SetFrameHex { get; set; }
    public string? AckFrameHex { get; set; }
    public byte? AckPayloadByte { get; set; }
    public CustomEqSnapshot? After { get; set; }
    public string? CustomAfterFrameHex { get; set; }
    public byte? PresetAfterCode { get; set; }
    public string? PresetAfterName { get; set; }
    public string? PresetAfterFrameHex { get; set; }
}
