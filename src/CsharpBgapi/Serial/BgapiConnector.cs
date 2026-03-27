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
    private readonly CsharpBgapiOptions _config;
    private SerialPort? _port;
    private readonly Lock _sendLock = new();
    private readonly Lock _receiveLock = new();
    private readonly ILogger _logger;
    private IReadOnlySet<byte>? _knownDeviceIds;
    private bool _disposed;

    public bool IsOpen => _port?.IsOpen == true;

    public BgapiConnector() : this((ILogger?)null) { }

    public BgapiConnector(ILogger? logger)
        : this(Options.Create(new CsharpBgapiOptions()), logger ?? NullLogger.Instance) { }

    public BgapiConnector(IOptions<CsharpBgapiOptions> options, ILogger<BgapiConnector> logger)
        : this(options, (ILogger)logger) { }

    internal BgapiConnector(IOptions<CsharpBgapiOptions> options, ILogger logger)
    {
        _config = options.Value;
        _logger = logger;
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

        _logger.LogInformation("Opening serial port {PortName} at {BaudRate} baud, handshake={Handshake}", portName, effectiveBaudRate, handshake);

        _port = new SerialPort(portName, effectiveBaudRate, Parity.None, 8, StopBits.One)
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
            _port.RtsEnable = true;
        }

        _port.Open();
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

    public BgapiMessage? ReadMessage(BgapiProtocol protocol, TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_port is not { IsOpen: true })
            throw new InvalidOperationException("Serial port is not open");

        lock (_receiveLock)
        {
            _port.ReadTimeout = (int)timeout.TotalMilliseconds;

            try
            {
                // Read first header byte with device ID validation
                byte firstByte;
                while (true)
                {
                    int b = _port.ReadByte();
                    if (b < 0) return null;
                    firstByte = (byte)b;

                    // Validate device ID if known IDs are configured
                    if (_knownDeviceIds is not null)
                    {
                        byte deviceId = (byte)((firstByte >> 3) & 0x0F);
                        if (!_knownDeviceIds.Contains(deviceId))
                        {
                            // Junk byte — skip and try next
                            _logger.LogDebug("Skipping junk byte 0x{Byte:X2} (deviceId={DeviceId}, known={KnownIds})",
                                firstByte, deviceId, string.Join(",", _knownDeviceIds));
                            continue;
                        }
                    }
                    break;
                }

                // Read remaining header bytes
                var headerBuf = new byte[BgapiHeader.Size];
                headerBuf[0] = firstByte;
                int read = ReadExact(headerBuf, 1, BgapiHeader.Size - 1);
                if (read < BgapiHeader.Size - 1) return null;

                var header = BgapiHeader.Parse(headerBuf);

                _logger.LogDebug("Read header: type=0x{Type:X2} deviceId={DeviceId} class={ClassIndex} index={CommandIndex} payloadLen={PayloadLen}",
                    header.MessageType, header.DeviceId, header.ClassIndex, header.CommandIndex, header.PayloadLength);

                byte[] fullMessage;
                if (header.PayloadLength > 0)
                {
                    var payloadBuf = new byte[header.PayloadLength];
                    read = ReadExact(payloadBuf, 0, header.PayloadLength);
                    if (read < header.PayloadLength) return null;

                    fullMessage = new byte[BgapiHeader.Size + header.PayloadLength];
                    headerBuf.CopyTo(fullMessage, 0);
                    payloadBuf.CopyTo(fullMessage, BgapiHeader.Size);
                }
                else
                {
                    fullMessage = headerBuf;
                }

                return protocol.DecodeMessage(fullMessage);
            }
            catch (TimeoutException)
            {
                _logger.LogTrace("ReadMessage timed out (expected in polling loop)");
                return null;
            }
        }
    }

    private int ReadExact(byte[] buffer, int offset, int count)
    {
        int totalRead = 0;
        int retries = 0;
        int maxRetries = _config.ReadExactMaxRetries;
        while (totalRead < count && retries < maxRetries)
        {
            try
            {
                int read = _port!.Read(buffer, offset + totalRead, count - totalRead);
                if (read == 0) break;
                totalRead += read;
                retries = 0; // reset on progress
            }
            catch (TimeoutException)
            {
                retries++;
                _logger.LogDebug("ReadExact timeout retry {Retry}/{MaxRetries}", retries, maxRetries);
            }
        }
        return totalRead;
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
