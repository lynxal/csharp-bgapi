using System.Collections.Concurrent;
using System.IO.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CsharpBgapi.Events;
using CsharpBgapi.Protocol;
using CsharpBgapi.Serial;

namespace CsharpBgapi;

/// <summary>
/// High-level BGAPI device interface. Combines serial connector, protocol, and event queue.
/// This is the main entry point for BGAPI communication, replacing BGLib + BGLibExt.
/// </summary>
public sealed class BgapiDevice : IDisposable
{
    private readonly CsharpBgapiOptions _config;
    private readonly BgapiConnector _connector;
    private readonly BgapiProtocol _protocol;
    private readonly BgapiEventQueue _eventQueue;
    private readonly XapiDefinitions _definitions;
    private readonly ILogger _logger;
    private readonly TimeSpan _readerLoopReadTimeout;
    private readonly TimeSpan _stopReaderTimeout;
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly Lock _lifecycleLock = new();
    // For each header with a written command whose answer never came (it timed out, or its caller
    // stopped waiting): the numbers of the first and last such send. That answer can still arrive
    // and would complete a repeat of the command, because a BGAPI response identifies its command
    // only by header. First is a lower bound once a fence has been answered ambiguously; see
    // RecordAnswered. One entry per header, so an outage of any length keeps it small. Guarded by
    // _commandLock, as is _sends.
    private readonly Dictionary<(byte Device, byte Class, byte Command), (long First, long Last)> _unanswered = [];
    private long _sends;
    // Side-effect-free commands that drain the NCP before such a repeat. Two, so that a repeat of
    // one is fenced by the other.
    private static readonly (string Api, string Class, string Command)[] FenceCommands =
        [("bt", "system", "hello"), ("bt", "system", "get_version")];
    private readonly ConcurrentDictionary<string, List<Action<BgapiMessage>>> _eventHandlers = new();
    // volatile: written by the command thread, read by the reader thread
    private volatile PendingCommand? _pendingCommand;
    private CancellationTokenSource? _readerCts;
    private Thread? _readerThread;
    private bool _disposed;
    internal Action<CancellationTokenSource, TimeSpan> ScheduleResponseTimeout { get; set; } =
        static (source, timeout) => source.CancelAfter(timeout);

    public BgapiEventQueue EventQueue => _eventQueue;
    public BgapiProtocol Protocol => _protocol;
    public XapiDefinitions Definitions => _definitions;
    public bool IsOpen => _connector.IsOpen;

    /// <summary>
    /// Total bytes discarded while resyncing the serial stream since the port was opened. A non-zero
    /// and growing value means the link is losing bytes.
    /// </summary>
    public long DroppedByteCount => _connector.DroppedByteCount;

    /// <summary>
    /// Default timeout for waiting for command responses.
    /// </summary>
    public TimeSpan ResponseTimeout { get; set; }

    public BgapiDevice() : this((ILogger?)null) { }

    public BgapiDevice(ILogger? logger)
        : this(Options.Create(new CsharpBgapiOptions()), logger ?? NullLogger.Instance) { }

    // loggerFactory is optional, since a host may register no logging. AddCsharpBgapi calls this
    // constructor itself. Left to DI, a host with no ILoggerFactory got BgapiDevice(ILogger?), which
    // builds default options, so the configure delegate was silently ignored.
    public BgapiDevice(IOptions<CsharpBgapiOptions> options, ILoggerFactory? loggerFactory = null)
        : this(options,
            (loggerFactory ??= NullLoggerFactory.Instance).CreateLogger<BgapiDevice>(),
            new BgapiConnector(options, loggerFactory.CreateLogger<BgapiConnector>()),
            new BgapiEventQueue(options, loggerFactory.CreateLogger<BgapiEventQueue>())) { }

    internal BgapiDevice(IOptions<CsharpBgapiOptions> options, BgapiConnector connector, ILogger? logger = null)
        : this(options, logger ?? NullLogger.Instance, connector) { }

    // Every constructor ends here, so each field is assigned once. The internal one used to chain to
    // the public one and then overwrite the connector and queue that constructor had just built.
    private BgapiDevice(IOptions<CsharpBgapiOptions> options, ILogger logger,
        BgapiConnector? connector = null, BgapiEventQueue? eventQueue = null)
    {
        _config = options.Value;
        _logger = logger;
        _definitions = new XapiDefinitions();
        _protocol = new BgapiProtocol(_definitions);
        _connector = connector ?? new BgapiConnector(options, logger);
        _eventQueue = eventQueue ?? new BgapiEventQueue(options, logger);
        ResponseTimeout = TimeSpan.FromSeconds(_config.ResponseTimeoutSeconds);
        _readerLoopReadTimeout = TimeSpan.FromMilliseconds(_config.ReaderLoopReadTimeoutMs);
        _stopReaderTimeout = TimeSpan.FromSeconds(_config.StopReaderTimeoutSeconds);
    }

    public ApiDefinition LoadXapi(string path)
    {
        var api = _definitions.LoadFromFile(path);
        _connector.SetKnownDeviceIds(_definitions.GetKnownDeviceIds());
        return api;
    }

    public ApiDefinition LoadXapiFromStream(Stream stream)
    {
        var api = _definitions.LoadFromStream(stream);
        _connector.SetKnownDeviceIds(_definitions.GetKnownDeviceIds());
        return api;
    }

    /// <summary>
    /// Loads the built-in default XAPI definitions (Bluetooth and Bluetooth Mesh)
    /// that are embedded in the assembly. Call this instead of <see cref="LoadXapi"/>
    /// when you don't have your own XAPI files.
    /// </summary>
    public void LoadDefaultXapis()
    {
        foreach (var resourceName in EmbeddedXapiResources.AllResourceNames)
        {
            using var stream = EmbeddedXapiResources.OpenResource(resourceName);
            _definitions.LoadFromStream(stream);
        }
        _connector.SetKnownDeviceIds(_definitions.GetKnownDeviceIds());
    }

    /// <summary>
    /// Every serial port name the machine reports. No vendor filtering is applied — picking the
    /// NCP is the caller's job.
    /// </summary>
    public static string[] FindPorts() => BgapiConnector.FindPorts();

    /// <summary>
    /// Opens the serial port and starts the background reader.
    /// </summary>
    /// <exception cref="InvalidOperationException">The device is already open, or the previous
    /// session's reader is still running because a subscriber callback has not returned.</exception>
    public void Open(string portName, int baudRate = 0, Handshake handshake = Handshake.None)
    {
        // Serialized with Close and Dispose. Two concurrent callers used to both pass the IsOpen
        // check below, both open a port, and the second overwrote the first's port and reader.
        lock (_lifecycleLock)
        {
            // A second Open used to go through: the connector replaced its port without closing the
            // old one, and StartReader started a second reader thread over the same connector, so two
            // readers raced for frames and the first one's thread and token source were never
            // stopped. Checked on the port rather than on the reader, so a device whose port went
            // away underneath it (the reader then exits on its own) can still be opened again.
            if (IsOpen)
                throw new InvalidOperationException("BgapiDevice is already open. Call Close() before opening it again.");

            // The reader of a port that went away without Close() may still be inside ReadMessage.
            // StartReader used to overwrite its thread and token source without stopping it, so it
            // carried on beside the new reader.
            StopReader();

            // A reader held past the join by a subscriber callback used to be forgotten, and once
            // the callback returned it dispatched the old port's message beside the new reader.
            if (_readerThread is not null)
                throw new InvalidOperationException(
                    "The previous session's reader is still running, most likely inside a subscriber callback that has not returned. Retry Open once it has.");

            _logger.LogInformation("BgapiDevice opening on {PortName}", portName);
            _connector.Open(portName, baudRate, handshake);
            StartReader();
        }
    }

    public void Close()
    {
        lock (_lifecycleLock)
        {
            _logger.LogInformation("BgapiDevice closing");
            StopReader();
            _connector.Close();
        }
    }

    /// <summary>
    /// Sends a BGAPI command and returns the full response including all parameters.
    /// Commands marked as no_return in XAPI return immediately without waiting for a response.
    /// Thread-safe: concurrent calls are serialized via command lock.
    /// </summary>
    /// <remarks>
    /// A response identifies its command only by header, so after a command times out, or its caller
    /// cancels once it was sent, its late answer could complete the next command with the same
    /// header. Such a repeat is therefore preceded by a side-effect-free fence, bt.system.hello or
    /// bt.system.get_version, preferring one with no unanswered send of its own: the NCP answers in
    /// order, so the fence's answer proves the late one has passed. When both have unanswered sends
    /// (retries through an outage), a fence's answer may be its own header's late one, which proves
    /// less, so more than one fence can be sent. If a fence goes unanswered, the repeat returns
    /// <see cref="SlStatus.Timeout"/> without being sent, and the next attempt fences again. An
    /// answered command whose header had no unanswered send clears the need for a fence.
    /// </remarks>
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
            // Checked once the lock is held, so a caller that was waiting for it when the device
            // was disposed fails here rather than on the connector.
            ObjectDisposedException.ThrowIf(_disposed, this);

            var data = _protocol.EncodeCommand(apiName, className, commandName, parameters);

            if (cmdDef.NoReturn)
            {
                _connector.SendCommand(data);
                return new BgapiCommandResponse { Status = SlStatus.OK };
            }

            var header = BgapiHeader.Parse(data);
            // Ends: every answered fence proves a later send answered than the one before it did.
            while (_unanswered.ContainsKey(Identity(header)))
            {
                if (ChooseFence(Identity(header)) is not var (fenceName, fence, fenceHeader))
                {
                    _logger.LogWarning(
                        "No bt.system fence command is loaded; sending {Api}.{Class}.{Command} although an earlier one's answer may still arrive",
                        apiName, className, commandName);
                    break;
                }

                _logger.LogInformation("Sending {Fence} to drain the answer of an unanswered {Api}.{Class}.{Command}",
                    fenceName, apiName, className, commandName);
                if (await ExchangeAsync(fence, fenceHeader, effectiveTimeout, ct) is null)
                {
                    _logger.LogWarning(
                        "Command {Api}.{Class}.{Command} not sent: an earlier one with its header is unanswered and the NCP did not answer {Fence}",
                        apiName, className, commandName, fenceName);
                    return new BgapiCommandResponse { Status = SlStatus.Timeout };
                }
            }

            var response = await ExchangeAsync(data, header, effectiveTimeout, ct);
            if (response is null)
            {
                _logger.LogWarning("Command {Api}.{Class}.{Command} timed out after {Timeout}ms",
                    apiName, className, commandName, effectiveTimeout.TotalMilliseconds);
                return new BgapiCommandResponse { Status = SlStatus.Timeout };
            }

            return new BgapiCommandResponse
            {
                Status = ResponseStatus(cmdDef, response),
                Parameters = response.Parameters,
                RawMessage = response
            };
        }
        finally
        {
            _commandLock.Release();
        }
    }

    /// <summary>
    /// Writes one command and waits for its response: null when it times out, OperationCanceledException
    /// when <paramref name="ct"/> is cancelled. Caller holds <see cref="_commandLock"/>.
    /// </summary>
    private async Task<BgapiMessage?> ExchangeAsync(byte[] data, BgapiHeader header, TimeSpan timeout, CancellationToken ct)
    {
        long send = _sends++;
        // Set up response TCS before sending to avoid race
        var tcs = InstallPendingCommand(header);

        try
        {
            _connector.SendCommand(data);

            // Wait for response with timeout. The timeout cancels the TCS itself, so the answer and
            // the timeout race on one completion and exactly one wins. Cancelling only a WaitAsync
            // could end the wait although the reader had completed the TCS: that answer went
            // unaccounted, and when it was the late one of an earlier fence the send's own late
            // answer went unrecorded and later completed another command.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var cancelOnTimeout = timeoutCts.Token.Register(
                static state => ((TaskCompletionSource<BgapiMessage>)state!).TrySetCanceled(), tcs);
            ScheduleResponseTimeout(timeoutCts, timeout);

            try
            {
                var response = await tcs.Task;
                RecordAnswered(Identity(header), send);
                return response;
            }
            catch (OperationCanceledException)
            {
                // Its answer may still arrive, and would complete a repeat of this command. One
                // routed as the timeout won is dropped by the reader, so this is always so.
                RecordUnanswered(Identity(header), send);
                if (ct.IsCancellationRequested)
                    throw;
                return null;
            }
        }
        finally
        {
            ClearPendingCommand();
        }
    }

    /// <summary>
    /// The fence to send before repeating a command with header <paramref name="repeat"/>: a fence
    /// command with another header, preferring one with no unanswered send (its answer is its own,
    /// so it proves every earlier send), else the one whose first unanswered send is latest. Null
    /// when no fence command is loaded.
    /// </summary>
    /// <remarks>
    /// A fence with unanswered sends of its own header used to be ruled out. After a few retries
    /// through an outage both were, and the repeat returned Timeout unsent from then on, even once
    /// the NCP answered again.
    /// </remarks>
    private (string Name, byte[] Data, BgapiHeader Header)? ChooseFence((byte, byte, byte) repeat)
    {
        (string, byte[], BgapiHeader)? best = null;
        long bestFirst = long.MinValue;
        foreach (var (api, cls, command) in FenceCommands)
        {
            byte[] data;
            try { data = _protocol.EncodeCommand(api, cls, command); }
            catch (KeyNotFoundException) { continue; }

            var header = BgapiHeader.Parse(data);
            if (Identity(header) == repeat)
                continue;

            long first = _unanswered.TryGetValue(Identity(header), out var sends) ? sends.First : long.MaxValue;
            if (first > bestFirst)
            {
                best = ($"{api}.{cls}.{command}", data, header);
                bestFirst = first;
            }
        }

        return best;
    }

    /// <summary>
    /// Accounts for a matched answer to send number <paramref name="send"/>. With no earlier send of
    /// its header unanswered the answer is its own, and the NCP answers in order, so every earlier
    /// send has been answered or dropped. Otherwise (only a fence gets here) it may be the late
    /// answer of the first such send. That still proves every send before that one, and this send
    /// may yet be answered.
    /// </summary>
    private void RecordAnswered((byte, byte, byte) identity, long send)
    {
        if (!_unanswered.TryGetValue(identity, out var own))
        {
            _unanswered.Clear();
            return;
        }

        // own.First is at or before the header's first unanswered send, so any other header's send
        // numbered up to it came before that send.
        long proven = own.First;
        foreach (var (key, sends) in _unanswered.ToArray())
        {
            if (key == identity)
                _unanswered[key] = (proven + 1, send);
            else if (sends.Last <= proven)
                _unanswered.Remove(key);
            else
                _unanswered[key] = (Math.Max(sends.First, proven + 1), sends.Last);
        }
    }

    private void RecordUnanswered((byte, byte, byte) identity, long send) =>
        _unanswered[identity] = _unanswered.TryGetValue(identity, out var sends) ? (sends.First, send) : (send, send);

    private static (byte Device, byte Class, byte Command) Identity(in BgapiHeader header) =>
        (header.DeviceId, header.ClassIndex, header.CommandIndex);

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

    /// <summary>
    /// The status a command's response reports: its errorcode-typed return parameter mapped to
    /// <see cref="SlStatus"/>, <see cref="SlStatus.OK"/> when the command declares no errorcode
    /// return, or <see cref="SlStatus.Fail"/> when the decoded payload ended before it.
    /// </summary>
    internal static SlStatus ResponseStatus(CommandDefinition command, BgapiMessage response)
    {
        var errorCodeParam = BgapiProtocol.FindErrorCodeParamName(command);
        if (errorCodeParam is null)
            return SlStatus.OK;

        // A payload that ends before the errorcode (a truncated frame, or one resynced out of
        // junk) used to report OK, and the caller carried on as if the NCP had accepted the command.
        return response.Parameters is not null && response.TryGetParameter<ushort>(errorCodeParam, out var result)
            ? SlStatusExtensions.FromUint(result)
            : SlStatus.Fail;
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

    // An unresolvable class or event name must throw, not compose an id. The lookup used to
    // start clsIdx/evtIdx at 0 and assign only on a match, so a typo'd or firmware-renamed name
    // returned the id of class 0 / event 0 — the *EventFilter callers then filtered a different
    // event than the caller named, and the NCP answered OK. GetDeviceId below already throws
    // KeyNotFoundException for an unknown api; class and event names now fail the same way.
    internal uint GetEventId(string apiName, string className, string eventName)
    {
        var devId = _definitions.GetDeviceId(apiName);
        var cls = _definitions.GetClass(apiName, className);
        var evt = cls.Events.FirstOrDefault(e => e.Name == eventName)
            ?? throw new KeyNotFoundException($"Event '{eventName}' not found in {apiName}.{className}");

        var clsIdx = cls.Index;
        var evtIdx = evt.Index;

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
        // Each reader gets its own token rather than re-reading _readerCts: a reader that outlived
        // StopReader's join would otherwise pick up the next reader's uncancelled token. The token
        // is read here, not on the new thread: StopReader may dispose the source before that thread
        // runs, and CancellationTokenSource.Token then throws, unhandled, and ends the process.
        _readerCts = new CancellationTokenSource();
        var token = _readerCts.Token;
        _readerThread = new Thread(() => ReaderLoop(token))
        {
            IsBackground = true,
            Name = "BgapiReader"
        };
        _readerThread.Start();
    }

    private void StopReader()
    {
        _readerCts?.Cancel();

        // A reader still inside a subscriber callback can outlast the join. It is kept rather than
        // forgotten, so the next StopReader joins it again and Open refuses to start a second one.
        if (_readerThread?.Join(_stopReaderTimeout) == false)
        {
            _logger.LogWarning("BgapiDevice reader did not stop within {Timeout}s", _stopReaderTimeout.TotalSeconds);
            return;
        }

        _readerCts?.Dispose();
        _readerCts = null;
        _readerThread = null;
    }

    private void ReaderLoop(CancellationToken ct)
    {
        _logger.LogDebug("ReaderLoop started");
        try
        {
            while (!ct.IsCancellationRequested && _connector.IsOpen)
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

                    // Close cancelled this reader while it was reading: the message belongs to the
                    // closed session and must reach neither the queue, a pending command nor a
                    // subscriber. Checking only at the loop head let it through.
                    if (ct.IsCancellationRequested)
                        break;

                    if (message.IsResponse)
                    {
                        HandleResponseMessage(message);
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

    /// <summary>
    /// Routes a response message to the in-flight command, discarding responses whose
    /// header identity does not match. Without the identity check, a late response from
    /// a timed-out command A completes the TCS of the next command B with A's payload —
    /// B then reports a wrong (often falsely successful) result. A late response with B's own
    /// header cannot be told apart here; <see cref="SendCommandAsync"/> fences such a repeat.
    /// </summary>
    internal void HandleResponseMessage(BgapiMessage message)
    {
        var pending = _pendingCommand;
        if (pending is null)
        {
            _logger.LogDebug("Dropping response — no pending TCS");
            return;
        }

        if (message.DeviceId != pending.DeviceId ||
            message.ClassIndex != pending.ClassIndex ||
            message.CommandIndex != pending.CommandIndex)
        {
            _logger.LogWarning(
                "Dropping stale response dev={Dev} cls={Cls} idx={Idx} — in-flight command is dev={PendingDev} cls={PendingCls} idx={PendingIdx}",
                message.DeviceId, message.ClassIndex, message.CommandIndex,
                pending.DeviceId, pending.ClassIndex, pending.CommandIndex);
            return;
        }

        _logger.LogDebug("Completing pending response TCS");
        if (!pending.Tcs.TrySetResult(message))
            _logger.LogDebug("Dropping response — its command timed out or was cancelled as it arrived");
    }

    internal TaskCompletionSource<BgapiMessage> InstallPendingCommand(in BgapiHeader commandHeader)
    {
        var tcs = new TaskCompletionSource<BgapiMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCommand = new PendingCommand(tcs, commandHeader.DeviceId, commandHeader.ClassIndex, commandHeader.CommandIndex);
        return tcs;
    }

    internal void ClearPendingCommand() => _pendingCommand = null;

    private sealed record PendingCommand(
        TaskCompletionSource<BgapiMessage> Tcs,
        byte DeviceId,
        byte ClassIndex,
        byte CommandIndex);

    internal void DispatchToHandlers(BgapiMessage message)
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
        lock (_lifecycleLock)
        {
            if (_disposed) return;
            _disposed = true;
            Close();
            _connector.Dispose();
            // _commandLock is deliberately not disposed. A command still holding it would throw from
            // Release over its own outcome, and callers waiting for it would never be released. It
            // owns no wait handle while AvailableWaitHandle is never read, so nothing leaks.
        }
    }
}
