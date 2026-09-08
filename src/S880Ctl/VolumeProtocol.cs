namespace S880Ctl;

internal static class EdifierS880Mk2CnVolumeProtocol
{
    public const byte ExpectedMaximum = 30;
    public static byte[] Query => EdifierS880Mk2CnProtocol.Build(0x66, []);

    public static byte[] Set(int value)
    {
        if (value is < 0 or > ExpectedMaximum)
            throw new ArgumentOutOfRangeException(nameof(value), $"volume must be from 0 to {ExpectedMaximum}");
        return EdifierS880Mk2CnProtocol.Build(0x67, [(byte)value]);
    }

    public static VolumeSnapshot Parse(EcFrame frame, string context)
    {
        if (frame.Command != 0x66 || frame.Payload.Length != 2)
            throw new VolumeProtocolException($"{context} returned an unexpected volume frame: {frame.RawHex}");
        var maximum = frame.Payload[0];
        var current = frame.Payload[1];
        if (maximum != ExpectedMaximum)
            throw new VolumeProtocolException($"{context} reported maximum {maximum}; expected {ExpectedMaximum} for this S880 MKII profile");
        if (current > maximum)
            throw new VolumeProtocolException($"{context} reported current volume {current} above maximum {maximum}");
        return new(maximum, current, frame.RawHex);
    }
}

internal static class EcVolumeController
{
    public static async Task<VolumeProtocolOutcome> ExecuteAsync(
        VolumeOperation operation,
        int? requested,
        IEcExchange exchange,
        CancellationToken cancellationToken)
    {
        if (operation == VolumeOperation.Set && requested is null)
            throw new ArgumentException("volume set requires a value", nameof(requested));
        if (requested is not null && (requested is < 0 or > EdifierS880Mk2CnVolumeProtocol.ExpectedMaximum))
            throw new ArgumentOutOfRangeException(nameof(requested));

        var (productName, identity) = await EcEqController.VerifyIdentityAsync(exchange, cancellationToken);
        using var currentWaiter = exchange.RegisterWaiter(frame => frame.Command == 0x66);
        var query = EdifierS880Mk2CnVolumeProtocol.Query;
        await exchange.WriteAsync(query, cancellationToken);
        var currentFrame = await currentWaiter.WaitAsync(cancellationToken);
        var current = EdifierS880Mk2CnVolumeProtocol.Parse(currentFrame, "initial volume query");

        if (operation == VolumeOperation.Get)
        {
            return new(
                productName,
                identity.RawHex,
                current.Maximum,
                current.Current,
                null,
                Convert.ToHexString(query),
                current.FrameHex,
                null,
                null,
                current.Current,
                current.FrameHex,
                "initial-query",
                exchange.WritesHex.ToArray());
        }

        var target = requested!.Value;
        if (current.Current == target)
        {
            return new(
                productName,
                identity.RawHex,
                current.Maximum,
                current.Current,
                target,
                Convert.ToHexString(query),
                current.FrameHex,
                null,
                null,
                current.Current,
                current.FrameHex,
                "already-current",
                exchange.WritesHex.ToArray());
        }

        using var ackWaiter = exchange.RegisterWaiter(frame => frame.Command == 0x67);
        var setFrame = EdifierS880Mk2CnVolumeProtocol.Set(target);
        await exchange.WriteAsync(setFrame, cancellationToken);
        var ack = await ackWaiter.WaitAsync(cancellationToken);
        if (ack.Payload.Length != 2 || ack.Payload[0] != EdifierS880Mk2CnVolumeProtocol.ExpectedMaximum || ack.Payload[1] != target)
            throw new VolumeProtocolException($"volume-set response did not confirm maximum {EdifierS880Mk2CnVolumeProtocol.ExpectedMaximum} and requested level {target}: {ack.RawHex}");

        using var readbackWaiter = exchange.RegisterWaiter(frame => frame.Command == 0x66);
        await exchange.WriteAsync(EdifierS880Mk2CnVolumeProtocol.Query, cancellationToken);
        var readbackFrame = await readbackWaiter.WaitAsync(cancellationToken);
        var readback = EdifierS880Mk2CnVolumeProtocol.Parse(readbackFrame, "volume readback");
        if (readback.Current != target)
            throw new VolumeProtocolException($"volume readback was {readback.Current}, expected {target}");

        return new(
            productName,
            identity.RawHex,
            current.Maximum,
            current.Current,
            target,
            Convert.ToHexString(query),
            current.FrameHex,
            Convert.ToHexString(setFrame),
            ack.RawHex,
            readback.Current,
            readback.FrameHex,
            "explicit-volume-query-after-ack",
            exchange.WritesHex.ToArray());
    }
}

internal sealed class VolumeProtocolException(string message) : Exception(message);
internal sealed record VolumeSnapshot(byte Maximum, byte Current, string FrameHex);
internal sealed record VolumeProtocolOutcome(
    string ProductName,
    string IdentityFrameHex,
    byte Maximum,
    byte CurrentBefore,
    int? Requested,
    string QueryFrameHex,
    string CurrentBeforeFrameHex,
    string? SetFrameHex,
    string? AckFrameHex,
    byte CurrentAfter,
    string CurrentAfterFrameHex,
    string Verification,
    IReadOnlyList<string> WritesHex);
