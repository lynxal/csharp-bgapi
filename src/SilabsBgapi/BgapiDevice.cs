using System.Collections.Concurrent;
using System.IO.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SilabsBgapi.Events;
using SilabsBgapi.Protocol;
using SilabsBgapi.Serial;

namespace SilabsBgapi;

/// <summary>
/// High-level BGAPI device interface. Combines serial connector, protocol, and event queue.
/// This is the main entry point for BGAPI communication, replacing BGLib + BGLibExt.
/// </summary>
public sealed class BgapiDevice : IDisposable
{
    private readonly SilabsBgapiOptions _config;
    private readonly BgapiConnector _connector;
    private readonly BgapiProtocol _protocol;
    private readonly BgapiEventQueue _eventQueue;
    private readonly XapiDefinitions _definitions;
    private readonly ILogger _logger;
    private readonly TimeSpan _readerLoopReadTimeout;
    private readonly TimeSpan _stopReaderTimeout;
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly ConcurrentDictionary<string, List<Action<BgapiMessage>>> _eventHandlers = new();
    private TaskCompletionSource<BgapiMessage>? _pendingResponse;
    private CancellationTokenSource? _readerCts;
    private Thread? _readerThread;
    private bool _disposed;

    public BgapiEventQueue EventQueue => _eventQueue;
    public BgapiProtocol Protocol => _protocol;
    public XapiDefinitions Definitions => _definitions;
    public bool IsOpen => _connector.IsOpen;

    /// <summary>
    /// Default timeout for waiting for command responses.
    /// </summary>
    public TimeSpan ResponseTimeout { get; set; }

    public BgapiDevice() : this((ILogger?)null) { }

    public BgapiDevice(ILogger? logger)
    {
        var options = Options.Create(new SilabsBgapiOptions());
        _config = options.Value;
        _logger = logger ?? NullLogger.Instance;
        _definitions = new XapiDefinitions();
        _protocol = new BgapiProtocol(_definitions);
        _connector = new BgapiConnector(options, _logger);
        _eventQueue = new BgapiEventQueue(options, _logger);
        ResponseTimeout = TimeSpan.FromSeconds(_config.ResponseTimeoutSeconds);
        _readerLoopReadTimeout = TimeSpan.FromMilliseconds(_config.ReaderLoopReadTimeoutMs);
        _stopReaderTimeout = TimeSpan.FromSeconds(_config.StopReaderTimeoutSeconds);
    }

    public BgapiDevice(IOptions<SilabsBgapiOptions> options, ILoggerFactory loggerFactory)
    {
        _config = options.Value;
        _logger = loggerFactory.CreateLogger<BgapiDevice>();
        _definitions = new XapiDefinitions();
        _protocol = new BgapiProtocol(_definitions);
        _connector = new BgapiConnector(options, loggerFactory.CreateLogger<BgapiConnector>());
        _eventQueue = new BgapiEventQueue(options, loggerFactory.CreateLogger<BgapiEventQueue>());
        ResponseTimeout = TimeSpan.FromSeconds(_config.ResponseTimeoutSeconds);
        _readerLoopReadTimeout = TimeSpan.FromMilliseconds(_config.ReaderLoopReadTimeoutMs);
        _stopReaderTimeout = TimeSpan.FromSeconds(_config.StopReaderTimeoutSeconds);
    }

    public void LoadXapi(string path)
    {
        _definitions.LoadFromFile(path);
        _connector.SetKnownDeviceIds(_definitions.GetKnownDeviceIds());
    }

    public void LoadXapiFromStream(Stream stream)
    {
        _definitions.LoadFromStream(stream);
        _connector.SetKnownDeviceIds(_definitions.GetKnownDeviceIds());
    }

    public void Open(string portName, int baudRate = 0, Handshake handshake = Handshake.None)
    {
        _logger.LogInformation("BgapiDevice opening on {PortName}", portName);
        _connector.Open(portName, baudRate, handshake);
        StartReader();
    }

    public void Close()
    {
        _logger.LogInformation("BgapiDevice closing");
        StopReader();
        _connector.Close();
    }

    /// <summary>
    /// Sends a BGAPI command and returns the full response including all parameters.
    /// Commands marked as no_return in XAPI return immediately without waiting for a response.
    /// Thread-safe: concurrent calls are serialized via command lock.
    /// </summary>
    public async Task<BgapiCommandResponse> SendCommandAsync(
        string apiName,
        string className,
        string commandName,
        Dictionary<string, object>? parameters = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var cmdDef = _definitions.GetCommand(apiName, className, commandName);
        var effectiveTimeout = timeout ?? ResponseTimeout;

        _logger.LogDebug("SendCommandAsync {Api}.{Class}.{Command} noReturn={NoReturn}",
            apiName, className, commandName, cmdDef.NoReturn);

        await _commandLock.WaitAsync(ct);
        try
        {
            var data = _protocol.EncodeCommand(apiName, className, commandName, parameters);

            if (cmdDef.NoReturn)
            {
                _connector.SendCommand(data);
                return new BgapiCommandResponse { Status = SlStatus.OK };
            }

            // Set up response TCS before sending to avoid race
            var tcs = new TaskCompletionSource<BgapiMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingResponse = tcs;

            try
            {
                _connector.SendCommand(data);

                // Wait for response with timeout
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(effectiveTimeout);

                BgapiMessage response;
                try
                {
                    response = await tcs.Task.WaitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _logger.LogWarning("Command {Api}.{Class}.{Command} timed out after {Timeout}ms",
                        apiName, className, commandName, effectiveTimeout.TotalMilliseconds);
                    return new BgapiCommandResponse { Status = SlStatus.Timeout };
                }

                // Extract status by scanning for errorcode-typed parameter
                var status = SlStatus.OK;
                var errorCodeParam = BgapiProtocol.FindErrorCodeParamName(cmdDef);
                if (errorCodeParam is not null &&
                    response.Parameters is not null &&
                    response.TryGetParameter<ushort>(errorCodeParam, out var result))
                {
                    status = SlStatusExtensions.FromUint(result);
                }

                return new BgapiCommandResponse
                {
                    Status = status,
                    Parameters = response.Parameters,
                    RawMessage = response
                };
            }
            finally
            {
                _pendingResponse = null;
            }
        }
        finally
        {
            _commandLock.Release();
        }
    }

    /// <summary>
    /// Synchronous convenience wrapper for SendCommandAsync.
    /// </summary>
    public BgapiCommandResponse SendCommand(
        string apiName,
        string className,
        string commandName,
        Dictionary<string, object>? parameters = null,
        TimeSpan? timeout = null)
    {
        return SendCommandAsync(apiName, className, commandName, parameters, timeout, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    public BgapiEventList WaitEvent(
        EventSelector selector,
        TimeSpan timeout,
        Dictionary<string, object>? paramSubs = null,
        CancellationToken ct = default)
    {
        return _eventQueue.WaitEvents(selector, timeout,
            maxEvents: 1,
            maxTime: timeout,
            finalEventCount: 1,
            paramSubs: paramSubs,
            ct: ct);
    }

    public BgapiEventList WaitEvents(
        EventSelector selector,
        TimeSpan timeout,
        int maxEvents = int.MaxValue,
        TimeSpan? maxTime = null,
        bool resetTimeoutOnEvent = false,
        int? finalEventCount = 1,
        int? softFinalEventCount = null,
        TimeSpan? softFinalEventProcTime = null,
        bool keepAllEvents = false,
        Dictionary<string, object>? paramSubs = null,
        BgapiEventList? selectedEvents = null,
        CancellationToken ct = default)
    {
        return _eventQueue.WaitEvents(selector, timeout, maxEvents, maxTime,
            resetTimeoutOnEvent, finalEventCount, softFinalEventCount,
            softFinalEventProcTime, keepAllEvents, paramSubs, selectedEvents, ct);
    }

    public Task<BgapiEventList> RetryUntilAsync(
        Func<Task<BgapiCommandResponse>> command,
        EventSelector? eventSelector,
        RetryParams? retryParams = null,
        IReadOnlyList<SlStatus>? retryCmdErrorCodes = null,
        EventSelector? retryEventSelector = null,
        int finalEventCount = 1,
        int? softFinalEventCount = null,
        TimeSpan? softFinalEventProcTime = null,
        bool keepAllEvents = false,
        bool retryIntRstOnEvt = false,
        CancellationToken ct = default)
    {
        return _eventQueue.RetryUntilAsync(command, eventSelector,
            retryParams ?? RetryParams.FromOptions(_config), retryCmdErrorCodes,
            retryEventSelector, finalEventCount, softFinalEventCount,
            softFinalEventProcTime, keepAllEvents, retryIntRstOnEvt, ct);
    }

    #region Event Subscription

    /// <summary>
    /// Subscribe to events by name. Handler is called on the reader thread.
    /// </summary>
    public void Subscribe(string eventName, Action<BgapiMessage> handler)
    {
        _logger.LogInformation("Subscribe to {EventName}", eventName);
        _eventHandlers.AddOrUpdate(eventName,
            _ => [handler],
            (_, list) =>
            {
                lock (list)
                {
                    if (!list.Contains(handler))
                        list.Add(handler);
                }
                return list;
            });
    }

    /// <summary>
    /// Unsubscribe from events by name.
    /// </summary>
    public void Unsubscribe(string eventName, Action<BgapiMessage> handler)
    {
        _logger.LogInformation("Unsubscribe from {EventName}", eventName);
        if (_eventHandlers.TryGetValue(eventName, out var list))
        {
            lock (list)
            {
                list.Remove(handler);
            }
        }
    }

    #endregion

    #region NCP Event Filtering

    /// <summary>
    /// Add an event to the NCP event filter. Filtered events will be blocked from being sent by the NCP.
    /// </summary>
    public BgapiCommandResponse AddEventFilter(string apiName, string className, string eventName)
    {
        var eventId = GetEventId(apiName, className, eventName);
        var payload = new byte[5];
        payload[0] = 0x00; // ADD opcode
        BitConverter.TryWriteBytes(payload.AsSpan(1), eventId);
        return SendCommand("bt", "user", "manage_event_filter",
            new Dictionary<string, object> { { "data", payload } });
    }

    /// <summary>
    /// Remove an event from the NCP event filter.
    /// </summary>
    public BgapiCommandResponse RemoveEventFilter(string apiName, string className, string eventName)
    {
        var eventId = GetEventId(apiName, className, eventName);
        var payload = new byte[5];
        payload[0] = 0x01; // REMOVE opcode
        BitConverter.TryWriteBytes(payload.AsSpan(1), eventId);
        return SendCommand("bt", "user", "manage_event_filter",
            new Dictionary<string, object> { { "data", payload } });
    }

    /// <summary>
    /// Reset the NCP event filter (allow all events).
    /// </summary>
    public BgapiCommandResponse ResetEventFilter()
    {
        var payload = new byte[] { 0x02 }; // RESET opcode
        return SendCommand("bt", "user", "manage_event_filter",
            new Dictionary<string, object> { { "data", payload } });
    }

    private uint GetEventId(string apiName, string className, string eventName)
    {
        var devId = _definitions.GetDeviceId(apiName);
        byte clsIdx = 0;
        byte evtIdx = 0;

        foreach (var c in _definitions.FindAllClasses(apiName))
        {
            if (c.Name == className)
            {
                clsIdx = c.Index;
                foreach (var e in c.Events)
                {
                    if (e.Name == eventName)
                    {
                        evtIdx = e.Index;
                        break;
                    }
                }
                break;
            }
        }

        // Event identifier layout (from Python):
        // Bit 24-31: Event index
        // Bit 16-23: Class index
        // Bit 7: Event flag (1)
        // Bit 3-6: Device ID
        return (uint)((evtIdx & 0xFF) << 24 | (clsIdx & 0xFF) << 16 | 0x80 | (devId << 3));
    }

    #endregion

    #region Reader

    private void StartReader()
    {
        _readerCts = new CancellationTokenSource();
        _readerThread = new Thread(ReaderLoop)
        {
            IsBackground = true,
            Name = "BgapiReader"
        };
        _readerThread.Start();
    }

    private void StopReader()
    {
        _readerCts?.Cancel();
        _readerThread?.Join(_stopReaderTimeout);
        _readerCts?.Dispose();
        _readerCts = null;
        _readerThread = null;
    }

    private void ReaderLoop()
    {
        _logger.LogDebug("ReaderLoop started");
        try
        {
            while (_readerCts is { IsCancellationRequested: false } && _connector.IsOpen)
            {
                try
                {
                    var message = _connector.ReadMessage(_protocol, _readerLoopReadTimeout);
                    if (message is null)
                        continue;

                    _logger.LogDebug("Received message: {Type} name={EventName} dev={DeviceId} cls={ClassIndex} idx={CommandIndex} payloadLen={PayloadLen}",
                        message.IsEvent ? "Event" : "Response",
                        message.EventName ?? "(null)",
                        message.DeviceId,
                        message.ClassIndex,
                        message.CommandIndex,
                        message.Header.PayloadLength);

                    if (message.IsResponse)
                    {
                        // Complete pending command response
                        var tcs = _pendingResponse;
                        if (tcs is not null)
                        {
                            _logger.LogDebug("Completing pending response TCS");
                            tcs.TrySetResult(message);
                        }
                        else
                        {
                            _logger.LogDebug("Dropping response — no pending TCS");
                        }
                    }
                    else
                    {
                        // Event: enqueue for WaitEvents consumers
                        _eventQueue.Enqueue(message);

                        // Dispatch to subscribed handlers
                        DispatchToHandlers(message);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ReaderLoop exception");
                    Thread.Sleep(10);
                }
            }
        }
        finally
        {
            _logger.LogDebug("ReaderLoop exited");
        }
    }

    private void DispatchToHandlers(BgapiMessage message)
    {
        if (message.EventName is null)
        {
            _logger.LogDebug("Event has null EventName (dev={DeviceId} cls={ClassIndex} idx={CommandIndex}) — skipping dispatch",
                message.DeviceId, message.ClassIndex, message.CommandIndex);
            return;
        }

        if (_eventHandlers.TryGetValue(message.EventName, out var handlers))
        {
            // Copy list to avoid holding lock during invocation
            Action<BgapiMessage>[] snapshot;
            lock (handlers)
            {
                snapshot = [.. handlers];
            }

            _logger.LogDebug("Dispatching {EventName} to {HandlerCount} handler(s)", message.EventName, snapshot.Length);

            foreach (var handler in snapshot)
            {
                try
                {
                    handler(message);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Handler exception for {EventName}", message.EventName);
                }
            }
        }
        else
        {
            _logger.LogDebug("No handlers registered for {EventName}", message.EventName);
        }
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
        _connector.Dispose();
        _commandLock.Dispose();
    }
}
