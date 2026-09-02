using System.Diagnostics;
using System.IO.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CsharpBgapi.Protocol;

namespace CsharpBgapi.Serial;

/// <summary>
/// Serial port connector for BGAPI communication.
/// Handles raw I/O with framing, device ID validation, and thread-safe access.
/// </summary>
public sealed class BgapiConnector : IDisposable
{
    /// <summary>
    /// Holds bytes that have arrived but not yet formed a complete frame. Sized well past the
    /// largest possible frame (4-byte header + 2047-byte payload) plus one read.
    /// </summary>
    private const int RxBufferCapacity = 8192;

    /// <summary>Bytes taken off the port in one poll before being handed to the accumulator.</summary>
    private const int ReadChunkSize = 4096;

    private readonly CsharpBgapiOptions _config;
    private SerialPort? _port;
    private readonly Lock _sendLock = new();
    private readonly Lock _receiveLock = new();
    private readonly ILogger _logger;
    private IReadOnlySet<byte>? _knownDeviceIds;
    private readonly byte[] _rxBuffer = new byte[RxBufferCapacity];
    private readonly byte[] _readChunk = new byte[ReadChunkSize];
    private int _rxCount;
    private long _droppedByteCount;
    // Stopwatch timestamp at which the header now at the front of the accumulator was first seen
    // waiting for its payload; null when no such candidate is pending.
    private long? _candidateSince;
    // True once the current resync episode has been reported at Warning. Cleared when a frame is
    // delivered, so a persistently junk stream costs one warning rather than one per poll.
    private bool _resyncLogged;
    private bool _disposed;

    public bool IsOpen => _port?.IsOpen == true;

    /// <summary>
    /// Total bytes discarded while resyncing the stream since the port was opened. A non-zero and
    /// growing value means the link is losing bytes — the condition that used to surface only as a
    /// downstream command timeout.
    /// </summary>
    /// <remarks>
    /// Read atomically rather than under <c>_receiveLock</c> on purpose: that lock is held across a
    /// blocking <see cref="SerialPort.Read(byte[], int, int)"/>, so taking it here would stall a
    /// caller polling a diagnostic for as long as the read timeout.
    /// </remarks>
    public long DroppedByteCount => Interlocked.Read(ref _droppedByteCount);

    /// <summary>
    /// How long a resolvable header may sit at the front of the accumulator waiting for a payload
    /// before it is abandoned. From <see cref="CsharpBgapiOptions.PartialFrameTimeoutMs"/>.
    /// </summary>
    internal TimeSpan StalledCandidateBudget { get; set; }

    public BgapiConnector() : this((ILogger?)null) { }

    public BgapiConnector(ILogger? logger)
        : this(Options.Create(new CsharpBgapiOptions()), logger ?? NullLogger.Instance) { }

    public BgapiConnector(IOptions<CsharpBgapiOptions> options, ILogger<BgapiConnector> logger)
        : this(options, (ILogger)logger) { }

    internal BgapiConnector(IOptions<CsharpBgapiOptions> options, ILogger logger)
    {
        _config = options.Value;
        _logger = logger;
        StalledCandidateBudget = TimeSpan.FromMilliseconds(Math.Max(1, _config.PartialFrameTimeoutMs));
    }

    /// <summary>
    /// Set known device IDs for header validation. Unknown device IDs in incoming
    /// messages will be treated as junk and skipped (matching Python behavior).
    /// </summary>
    public void SetKnownDeviceIds(IReadOnlySet<byte> deviceIds)
    {
        _knownDeviceIds = deviceIds;
    }

    public void Open(string portName, int baudRate = 0, Handshake handshake = Handshake.None)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var effectiveBaudRate = baudRate > 0 ? baudRate : _config.DefaultBaudRate;

        _logger.LogInformation(
            "Opening serial port {PortName} at {BaudRate} baud, handshake={Handshake}, partial-frame budget {BudgetMs}ms",
            portName, effectiveBaudRate, handshake, StalledCandidateBudget.TotalMilliseconds);

        var port = new SerialPort(portName, effectiveBaudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = _config.SerialReadTimeoutMs,
            WriteTimeout = _config.SerialWriteTimeoutMs,
            Handshake = handshake,
            DtrEnable = true,
        };

        // Match PySerial: assert RTS even without hardware flow control.
        // With Handshake.RequestToSend, the driver manages RTS automatically.
        if (handshake == Handshake.None)
        {
            port.RtsEnable = true;
        }

        try
        {
            port.Open();
        }
        catch
        {
            port.Dispose();
            throw;
        }

        // Publish the new port and reset the stream state together, and only once the port is
        // actually open. A reader thread blocked in _port.Read holds _receiveLock for up to the
        // read timeout; assigning _port before taking that lock would leave a window in which the
        // reader sees a new, not-yet-open port and throws on every iteration. A new port also
        // starts a new stream, so nothing buffered from the previous one is still in frame.
        lock (_receiveLock)
        {
            _port = port;
            _rxCount = 0;
            _candidateSince = null;
            _resyncLogged = false;
            Interlocked.Exchange(ref _droppedByteCount, 0);
        }
    }

    public void Close()
    {
        _logger.LogInformation("Closing serial port");

        if (_port is { IsOpen: true })
        {
            // Temporarily disable RTS/CTS before closing to avoid hang
            if (_port.Handshake == Handshake.RequestToSend ||
                _port.Handshake == Handshake.RequestToSendXOnXOff)
            {
                try { _port.Handshake = Handshake.None; }
                catch (Exception ex) { _logger.LogDebug(ex, "Failed to disable RTS/CTS handshake before closing"); }
            }
            _port.Close();
        }
    }

    public void SendCommand(byte[] data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_port is not { IsOpen: true })
            throw new InvalidOperationException("Serial port is not open");

        _logger.LogDebug("Sending command: {ByteCount} bytes", data);

        lock (_sendLock)
        {
            _port.Write(data, 0, data.Length);
        }
    }

    /// <summary>
    /// Reads the next complete BGAPI frame, or returns null when none has arrived yet.
    ///
    /// Bytes that do not yet form a whole frame stay in the accumulator for the next call. The
    /// earlier design read the header and payload straight off the port and, on a short read,
    /// returned null while discarding what it had already consumed — leaving the port mid-frame so
    /// the next call parsed payload bytes as a header. Combined with a resync that checked only the
    /// device-id nibble, that produced fabricated frames whose bogus payload length swallowed real
    /// responses (Hub firmware job 158, block 192, 2026-09-01).
    /// </summary>
    public BgapiMessage? ReadMessage(BgapiProtocol protocol, TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_port is not { IsOpen: true })
            throw new InvalidOperationException("Serial port is not open");

        lock (_receiveLock)
        {
            _port.ReadTimeout = (int)timeout.TotalMilliseconds;

            // A previous poll may already have buffered more than one frame's worth of bytes.
            var buffered = TakeBufferedFrame(protocol);
            if (buffered is not null) return buffered;

            int read;
            try
            {
                read = _port.Read(_readChunk, 0, _readChunk.Length);
            }
            catch (TimeoutException)
            {
                _logger.LogTrace("ReadMessage timed out (expected in polling loop)");
                // Still give a stalled candidate its chance to expire: a frame whose payload never
                // arrives is exactly the case that shows up as a quiet port.
                return TakeBufferedFrame(protocol);
            }

            AppendReceived(_readChunk.AsSpan(0, read));

            return TakeBufferedFrame(protocol);
        }
    }

    /// <summary>
    /// Adds freshly received bytes to the accumulator. Caller must hold <see cref="_receiveLock"/>.
    /// </summary>
    internal void AppendReceived(ReadOnlySpan<byte> bytes)
    {
        int overflow = _rxCount + bytes.Length - RxBufferCapacity;
        if (overflow > 0)
        {
            // Out of reach while the largest frame is 2051 bytes and one read is at most
            // ReadChunkSize, but the invariant is not enforced anywhere. An overflowing CopyTo
            // would throw before _rxCount advanced, so nothing would ever drain the accumulator
            // and every later poll would throw identically — a permanently dead receive path.
            // Resyncing is recoverable; wedging is not.
            int drop = Math.Min(overflow, _rxCount);
            ReportDropped(drop, "overflowed the receive accumulator");
            Consume(drop);
            _candidateSince = null;
        }

        bytes.CopyTo(_rxBuffer.AsSpan(_rxCount));
        _rxCount += bytes.Length;
    }

    /// <summary>
    /// Runs the frame scanner over the accumulator, removes whatever it consumed, reports any
    /// resync, and abandons a header whose payload has stopped arriving. Caller must hold
    /// <see cref="_receiveLock"/>.
    /// </summary>
    internal BgapiMessage? TakeBufferedFrame(BgapiProtocol protocol)
    {
        while (true)
        {
            FrameScanResult result;
            try
            {
                result = TryExtractFrame(_rxBuffer.AsSpan(0, _rxCount), protocol, _knownDeviceIds);
            }
            catch (Exception ex)
            {
                // Nothing has been consumed at this point, so letting the exception out would
                // leave the offending bytes at the front of the accumulator for the next poll to
                // fail on identically, forever. Resync past them like any other bad frame.
                _logger.LogWarning(ex, "Serial resync: decoding a frame threw; dropping one byte and rescanning");
                ReportDropped(1, "could not be decoded");
                Consume(1);
                continue;
            }

            if (result.DroppedBytes > 0)
            {
                LogRejectedHeader();
                ReportDropped(result.DroppedBytes, "started no known BGAPI frame");
            }

            if (result.Consumed > 0)
            {
                Consume(result.Consumed);
                // The front of the buffer moved, so any pending candidate is a different one now.
                _candidateSince = null;
            }

            if (result.Message is not null)
            {
                _candidateSince = null;
                _resyncLogged = false;
                return result.Message;
            }

            // Fewer than four bytes left: waiting on a header, not on a payload.
            if (_rxCount < BgapiHeader.Size)
            {
                _candidateSince = null;
                return null;
            }

            // A resolvable header is waiting for its payload. Normally the rest is a poll away.
            if (_candidateSince is null)
            {
                _candidateSince = Stopwatch.GetTimestamp();
                return null;
            }

            if (Stopwatch.GetElapsedTime(_candidateSince.Value) < StalledCandidateBudget)
                return null;

            // Budget spent. The declared length cannot be satisfied by this frame's own bytes —
            // a payload byte was lost, or the length bits themselves are corrupt. Keeping the
            // candidate would fold the NEXT real frame's bytes into it and hand the in-flight
            // command a plausible-looking frame built from someone else's payload. The removed
            // ReadExact ladder could at least give up; this restores that, without the discard
            // that left the stream mid-frame. Drop one byte and rescan.
            //
            // The budget is per candidate, not per corrupt region: carrying the clock across the
            // drop would destroy a legitimate partial frame sitting at the tail of the buffer
            // right after the region. The cost that would buy is now negligible anyway — with
            // IsKnownHeader bounding the declared length, a mid-payload window that both resolves
            // to a real definition and declares a length that definition could produce is rare.
            ReportDropped(1, "began a frame whose payload never completed within the partial-frame budget");
            Consume(1);
            _candidateSince = null;
        }
    }

    /// <summary>
    /// The four rejected bytes are the only clue distinguishing line noise from a frame the loaded
    /// XAPI simply does not define — the version-skew case, which is otherwise indistinguishable
    /// from junk. Caller must hold <see cref="_receiveLock"/>.
    /// </summary>
    private void LogRejectedHeader()
    {
        if (!_logger.IsEnabled(LogLevel.Debug) || _rxCount < BgapiHeader.Size) return;

        var rejected = BgapiHeader.Parse(_rxBuffer);
        _logger.LogDebug(
            "Serial resync rejected header: isEvent={IsEvent} dev={Dev} cls={Cls} idx={Idx} payloadLen={PayloadLen}",
            rejected.IsEvent, rejected.DeviceId, rejected.ClassIndex, rejected.CommandIndex, rejected.PayloadLength);
    }

    private void ReportDropped(int count, string reason)
    {
        var total = Interlocked.Add(ref _droppedByteCount, count);

        // One warning per resync episode. A device emitting a boot banner, or a floating line,
        // would otherwise log on every poll and bury the signal the counter exists to give.
        if (_resyncLogged)
        {
            _logger.LogDebug(
                "Serial resync: dropped {Dropped} byte(s) that {Reason} ({Total} total since open)",
                count, reason, total);
            return;
        }

        _resyncLogged = true;
        _logger.LogWarning(
            "Serial resync: dropped {Dropped} byte(s) that {Reason} ({Total} total since open; further drops until the next good frame log at Debug)",
            count, reason, total);
    }

    private void Consume(int count)
    {
        _rxCount -= count;
        if (_rxCount > 0)
            Array.Copy(_rxBuffer, count, _rxBuffer, 0, _rxCount);
    }

    /// <summary>
    /// Scans <paramref name="buffer"/> for the next complete frame. Pure — no port involved — so the
    /// framing rules are unit-testable.
    ///
    /// A header is accepted only when <paramref name="knownDeviceIds"/> (when configured) admits its
    /// device id AND the loaded definitions resolve it to a real command or event whose payload can
    /// be as long as the header declares. A rejected header advances the scan by exactly ONE byte: a
    /// genuine frame start can sit at offset 1 of a rejected four-byte window, and skipping the
    /// window would step over it.
    ///
    /// When the frame's payload has not fully arrived, nothing is consumed beyond leading junk — the
    /// partial frame is left for the next poll to complete.
    /// </summary>
    internal static FrameScanResult TryExtractFrame(
        ReadOnlySpan<byte> buffer, BgapiProtocol protocol, IReadOnlySet<byte>? knownDeviceIds)
    {
        int offset = 0;

        while (buffer.Length - offset >= BgapiHeader.Size)
        {
            var header = BgapiHeader.Parse(buffer[offset..]);

            if (!IsPlausibleFrameStart(header, protocol, knownDeviceIds))
            {
                offset++;
                continue;
            }

            int frameLength = BgapiHeader.Size + header.PayloadLength;
            if (buffer.Length - offset < frameLength)
                break;

            var message = protocol.DecodeMessage(buffer.Slice(offset, frameLength));
            return new FrameScanResult(message, offset + frameLength, offset);
        }

        // Nothing decodable yet. Leading junk is consumed; the candidate frame start is kept.
        return new FrameScanResult(null, offset, offset);
    }

    private static bool IsPlausibleFrameStart(
        in BgapiHeader header, BgapiProtocol protocol, IReadOnlySet<byte>? knownDeviceIds)
    {
        return (knownDeviceIds is null || knownDeviceIds.Contains(header.DeviceId))
            && protocol.IsKnownHeader(header);
    }

    public static string[] FindSilabsPorts()
    {
        return SerialPort.GetPortNames();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
        _port?.Dispose();
    }
}

/// <summary>
/// Outcome of one frame scan over the receive accumulator.
/// </summary>
/// <param name="Message">The decoded frame, or null when none is complete yet.</param>
/// <param name="Consumed">Bytes to remove from the front of the buffer — leading junk plus the frame itself when one
/// was decoded.</param>
/// <param name="DroppedBytes">Bytes discarded because they started no known frame.</param>
internal readonly record struct FrameScanResult(BgapiMessage? Message, int Consumed, int DroppedBytes);
