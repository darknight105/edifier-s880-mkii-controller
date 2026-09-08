using System.Text;

namespace S880Ctl;

internal static class EdifierS880Mk2CnProtocol
{
    public const string ExpectedProductName = "EDIFIER S880 MKII";
    public static readonly Guid ServiceUuid = Guid.Parse("48098801-1a48-11e9-ab14-d663bd873d93");
    public static readonly Guid NotifyUuid = Guid.Parse("48090001-1a48-11e9-ab14-d663bd873d93");
    public static readonly Guid TransmitUuid = Guid.Parse("48090002-1a48-11e9-ab14-d663bd873d93");

    public static byte[] ProductIdentityQuery => Build(0xC9, []);
    public static byte[] SourceQuery => Build(0x61, []);
    public static byte[] SourceSet(SourceOperation operation) => Build(0x62, [0x10, SourceCode(operation)]);

    public static byte[] Build(byte command, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[payload.Length + 6];
        frame[0] = 0xAA;
        frame[1] = 0xEC;
        frame[2] = command;
        frame[3] = (byte)(payload.Length >> 8);
        frame[4] = (byte)payload.Length;
        payload.CopyTo(frame.AsSpan(5));
        frame[^1] = Checksum(frame.AsSpan(0, frame.Length - 1));
        return frame;
    }

    public static byte Checksum(ReadOnlySpan<byte> bytes)
    {
        var sum = 0;
        foreach (var value in bytes) sum = (sum + value) & 0xff;
        return (byte)sum;
    }

    public static byte SourceCode(SourceOperation operation) => operation switch
    {
        SourceOperation.Usb => 0x01,
        SourceOperation.LineIn1 => 0x02,
        SourceOperation.LineIn2 => 0x03,
        SourceOperation.Bluetooth => 0x04,
        SourceOperation.Optical => 0x05,
        SourceOperation.Coaxial => 0x06,
        _ => throw new ArgumentOutOfRangeException(nameof(operation), "get has no set payload")
    };

    public static string SourceName(byte code) => code switch
    {
        0x01 => "usb",
        0x02 => "line-in-1",
        0x03 => "line-in-2",
        0x04 => "bluetooth",
        0x05 => "optical",
        0x06 => "coaxial",
        _ => $"unknown-0x{code:X2}"
    };
}

internal sealed record EcFrame(byte Command, byte[] Payload, byte[] RawBytes)
{
    public string RawHex => Convert.ToHexString(RawBytes);
}

internal sealed class EcFrameParser
{
    private const int MaximumBufferLength = 4096;
    private const int MaximumPayloadLength = 512;
    private readonly List<byte> _buffer = [];

    public int BadChecksumCount { get; private set; }
    public int OversizeFrameCount { get; private set; }
    public int DiscardedByteCount { get; private set; }

    public IReadOnlyList<EcFrame> Push(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes) _buffer.Add(value);
        if (_buffer.Count > MaximumBufferLength)
        {
            var overflow = _buffer.Count - MaximumBufferLength;
            _buffer.RemoveRange(0, overflow);
            DiscardedByteCount += overflow;
        }

        var frames = new List<EcFrame>();
        while (true)
        {
            var header = FindHeader();
            if (header < 0)
            {
                var keepTrailingStart = _buffer.Count > 0 && _buffer[^1] == 0xBB;
                var remove = _buffer.Count - (keepTrailingStart ? 1 : 0);
                if (remove > 0)
                {
                    _buffer.RemoveRange(0, remove);
                    DiscardedByteCount += remove;
                }
                break;
            }

            if (header > 0)
            {
                _buffer.RemoveRange(0, header);
                DiscardedByteCount += header;
            }

            if (_buffer.Count < 6) break;
            var payloadLength = (_buffer[3] << 8) | _buffer[4];
            if (payloadLength > MaximumPayloadLength)
            {
                _buffer.RemoveAt(0);
                DiscardedByteCount++;
                OversizeFrameCount++;
                continue;
            }

            var frameLength = payloadLength + 6;
            if (_buffer.Count < frameLength) break;
            var raw = _buffer.GetRange(0, frameLength).ToArray();
            if (EdifierS880Mk2CnProtocol.Checksum(raw.AsSpan(0, raw.Length - 1)) != raw[^1])
            {
                _buffer.RemoveAt(0);
                DiscardedByteCount++;
                BadChecksumCount++;
                continue;
            }

            frames.Add(new(raw[2], raw.AsSpan(5, payloadLength).ToArray(), raw));
            _buffer.RemoveRange(0, frameLength);
        }

        return frames;
    }

    private int FindHeader()
    {
        for (var index = 0; index + 1 < _buffer.Count; index++)
        {
            if (_buffer[index] == 0xBB && _buffer[index + 1] == 0xEC) return index;
        }
        return -1;
    }
}

internal sealed class NotificationInbox
{
    private readonly object _sync = new();
    private readonly EcFrameParser _parser = new();
    private readonly List<EcFrameWaiter> _waiters = [];

    public EcFrameWaiter Register(Func<EcFrame, bool> predicate)
    {
        lock (_sync)
        {
            var waiter = new EcFrameWaiter(this, predicate);
            _waiters.Add(waiter);
            return waiter;
        }
    }

    public void Push(ReadOnlySpan<byte> bytes)
    {
        lock (_sync)
        {
            foreach (var frame in _parser.Push(bytes))
            {
                foreach (var waiter in _waiters.Where(waiter => waiter.Predicate(frame)).ToArray())
                {
                    _waiters.Remove(waiter);
                    waiter.Complete(frame);
                }
            }
        }
    }

    public ParserDiagnostics Diagnostics
    {
        get
        {
            lock (_sync) return new(_parser.BadChecksumCount, _parser.OversizeFrameCount, _parser.DiscardedByteCount);
        }
    }

    internal void Remove(EcFrameWaiter waiter)
    {
        lock (_sync) _waiters.Remove(waiter);
    }
}

internal sealed class EcFrameWaiter : IDisposable
{
    private readonly NotificationInbox _owner;
    private readonly TaskCompletionSource<EcFrame> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Func<EcFrame, bool> Predicate { get; }
    public Task<EcFrame> Task => _completion.Task;

    internal EcFrameWaiter(NotificationInbox owner, Func<EcFrame, bool> predicate)
    {
        _owner = owner;
        Predicate = predicate;
    }

    internal void Complete(EcFrame frame) => _completion.TrySetResult(frame);
    public Task<EcFrame> WaitAsync(CancellationToken cancellationToken) => Task.WaitAsync(cancellationToken);
    public void Dispose() => _owner.Remove(this);
}

internal sealed record ParserDiagnostics(int BadChecksumCount, int OversizeFrameCount, int DiscardedByteCount);

internal interface IEcExchange
{
    IReadOnlyList<string> WritesHex { get; }
    bool SetAttempted { get; }
    EcFrameWaiter RegisterWaiter(Func<EcFrame, bool> predicate);
    Task WriteAsync(byte[] frame, CancellationToken cancellationToken);
}

internal static class EcSourceController
{
    public static async Task<SourceProtocolOutcome> ExecuteAsync(
        SourceOperation operation,
        IEcExchange exchange,
        CancellationToken cancellationToken,
        TimeSpan? automaticStatusGrace = null)
    {
        using var identityWaiter = exchange.RegisterWaiter(frame => frame.Command == 0xC9);
        await WriteAsync(EdifierS880Mk2CnProtocol.ProductIdentityQuery);
        var identity = await identityWaiter.WaitAsync(cancellationToken);
        var productName = Encoding.ASCII.GetString(identity.Payload);
        if (!productName.Equals(EdifierS880Mk2CnProtocol.ExpectedProductName, StringComparison.Ordinal))
            throw new SourceProtocolException($"product identity mismatch: '{productName}'");

        using var currentWaiter = exchange.RegisterWaiter(frame => frame.Command == 0x61);
        await WriteAsync(EdifierS880Mk2CnProtocol.SourceQuery);
        var currentFrame = await currentWaiter.WaitAsync(cancellationToken);
        var currentCode = RequireSourceStatus(currentFrame, "initial source query");
        var currentName = EdifierS880Mk2CnProtocol.SourceName(currentCode);

        if (operation == SourceOperation.Get)
            return new(productName, identity.RawHex, currentCode, currentName, null, null, null, currentCode, currentName, "initial-query", exchange.WritesHex.ToArray());

        if (currentCode is < 0x01 or > 0x06)
            throw new SourceProtocolException($"current source code 0x{currentCode:X2} is not mapped for a verified source-set preflight");

        var targetCode = EdifierS880Mk2CnProtocol.SourceCode(operation);
        var targetName = EdifierS880Mk2CnProtocol.SourceName(targetCode);
        if (currentCode == targetCode)
            return new(productName, identity.RawHex, currentCode, currentName, targetCode, null, null, currentCode, targetName, "already-current", exchange.WritesHex.ToArray());

        using var ackWaiter = exchange.RegisterWaiter(frame => frame.Command == 0x62);
        using var targetWaiter = exchange.RegisterWaiter(frame =>
            frame.Command == 0x61 && frame.Payload is [0x10, var value] && value == targetCode);
        var setFrame = EdifierS880Mk2CnProtocol.SourceSet(operation);
        await WriteAsync(setFrame);
        var ack = await ackWaiter.WaitAsync(cancellationToken);
        if (ack.Payload is not [0x01]) throw new SourceProtocolException($"source-set ACK was not success: {ack.RawHex}");

        var grace = automaticStatusGrace ?? TimeSpan.FromSeconds(2);
        var winner = await Task.WhenAny(targetWaiter.Task, Task.Delay(grace, cancellationToken));
        string verification;
        EcFrame finalStatus;
        if (winner == targetWaiter.Task)
        {
            finalStatus = await targetWaiter.WaitAsync(cancellationToken);
            verification = "automatic-status-notification";
        }
        else
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteAsync(EdifierS880Mk2CnProtocol.SourceQuery);
            finalStatus = await targetWaiter.WaitAsync(cancellationToken);
            verification = "explicit-source-query-after-ack";
        }

        var finalCode = RequireSourceStatus(finalStatus, "post-set source status");
        if (finalCode != targetCode) throw new SourceProtocolException("post-set source status did not match the requested target");
        return new(productName, identity.RawHex, currentCode, currentName, targetCode, Convert.ToHexString(setFrame), ack.RawHex, finalCode, targetName, verification, exchange.WritesHex.ToArray());

        async Task WriteAsync(byte[] frame)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await exchange.WriteAsync(frame, cancellationToken);
        }
    }

    private static byte RequireSourceStatus(EcFrame frame, string context)
    {
        if (frame.Payload is not [0x10, var sourceCode])
            throw new SourceProtocolException($"{context} returned an unexpected payload: {frame.RawHex}");
        return sourceCode;
    }
}

internal sealed class SourceProtocolException(string message) : Exception(message);
internal sealed record SourceProtocolOutcome(
    string ProductName,
    string IdentityFrameHex,
    byte CurrentBeforeCode,
    string CurrentBefore,
    byte? RequestedCode,
    string? SetFrameHex,
    string? AckFrameHex,
    byte CurrentAfterCode,
    string CurrentAfter,
    string Verification,
    string[] WritesHex);
