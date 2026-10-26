using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CsharpBgapi.Protocol;

namespace CsharpBgapi.Events;

/// <summary>
/// Event queue with selector-based waiting. Ports BGLibExt event waiting patterns.
/// Events are enqueued from the reader thread and consumed by WaitEvents/RetryUntilAsync.
/// </summary>
/// <remarks>
/// A wait takes only the events its selector selects and leaves every other event where it is, in
/// arrival order, for other waiters. The earlier design dequeued everything, held the non-matching
/// events in a private list and re-enqueued them at the tail when the call returned. With two
/// consumers on one queue — the Hub's vendor-model receive loop polling every 200 ms and its
/// firmware clients waiting up to a chunk budget — a firmware wait kept the vendor-model events it
/// happened to win until its own window ended, order was lost on re-enqueue, a wait cancelled
/// mid-flight dropped what it held, and events no consumer ever selected were re-queued forever and
/// re-examined by every later call (2026-10-22, Hub NcpLifecycleService + BlobTransferClient).
/// Unclaimed events are now bounded by <see cref="CsharpBgapiOptions.MaxQueuedEvents"/>.
/// </remarks>
public sealed class BgapiEventQueue
{
    private readonly CsharpBgapiOptions _config;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    // Ascending by Seq. Events stay here until a waiter takes them, bounded by MaxQueuedEvents.
    private readonly List<QueuedEvent> _events = [];
    private readonly int _maxQueued;
    private long _nextSeq;
    private long _droppedEventCount;
    private bool _dropLogged;
    // Completed and replaced on every Enqueue. A waiter takes the current ticket under the same
    // lock that found nothing left to examine, so an arrival between that scan and the wait
    // completes the very ticket it is about to wait on.
    private TaskCompletionSource<bool> _arrival = NewTicket();
    // Validation can pause a signaled waiter before it scans; notification is not a reservation.
    internal Action? AfterArrival { get; set; }
    internal Action? BeforeArrival { get; set; }

    // ArrivedAt is the Stopwatch timestamp of the Enqueue, for telling an event that came before a
    // waiter's deadline from one that came after.
    private readonly record struct QueuedEvent(long Seq, BgapiMessage Message, long ArrivedAt);

    private static TaskCompletionSource<bool> NewTicket() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public BgapiEventQueue() : this((ILogger?)null) { }

    public BgapiEventQueue(ILogger? logger)
        : this(Options.Create(new CsharpBgapiOptions()), logger ?? NullLogger.Instance) { }

    public BgapiEventQueue(IOptions<CsharpBgapiOptions> options, ILogger<BgapiEventQueue> logger)
        : this(options, (ILogger)logger) { }

    internal BgapiEventQueue(IOptions<CsharpBgapiOptions> options, ILogger logger)
    {
        _config = options.Value;
        _logger = logger;
        _maxQueued = Math.Max(1, _config.MaxQueuedEvents);
    }

    /// <summary>Events currently retained: queued by the reader and not yet taken by a waiter.</summary>
    public int Count
    {
        get { lock (_gate) return _events.Count; }
    }

    /// <summary>
    /// Events evicted because the queue already held <see cref="CsharpBgapiOptions.MaxQueuedEvents"/>
    /// that no waiter had taken. A growing value means events are produced that nothing consumes.
    /// </summary>
    public long DroppedEventCount => Interlocked.Read(ref _droppedEventCount);

    public void Enqueue(BgapiMessage message)
    {
        TaskCompletionSource<bool> ticket;
        bool dropped = false, firstDrop = false;
        long total = 0;

        lock (_gate)
        {
            _events.Add(new QueuedEvent(_nextSeq++, message, Stopwatch.GetTimestamp()));
            if (_events.Count > _maxQueued)
            {
                _events.RemoveAt(0);
                total = Interlocked.Increment(ref _droppedEventCount);
                dropped = true;
                firstDrop = !_dropLogged;
                _dropLogged = true;
            }
            ticket = _arrival;
            _arrival = NewTicket();
        }

        ticket.TrySetResult(true);

        if (!dropped) return;
        // One warning per episode; Clear() starts a new one. The counter carries the rest.
        if (firstDrop)
        {
            _logger.LogWarning(
                "Event queue full ({Max} events): dropped the oldest unclaimed event ({Total} dropped so far). Events accumulate when no WaitEvents consumer takes them — raise CsharpBgapiOptions.MaxQueuedEvents, consume them, or Clear() the queue; further drops log at Debug",
                _maxQueued, total);
        }
        // Every event after the first drop of an episode lands here, on the reader thread; with Debug
        // off it used to pay for the params array and boxed counts LogDebug builds before asking.
        else if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Event queue full ({Max} events): dropped the oldest unclaimed event ({Total} dropped so far)",
                _maxQueued, total);
        }
    }

    /// <summary>
    /// Gets the oldest queued event, waiting up to timeout for one to arrive.
    /// </summary>
    public BgapiMessage? GetEvent(TimeSpan timeout)
    {
        var start = Stopwatch.GetTimestamp();
        while (true)
        {
            TaskCompletionSource<bool> arrival;
            lock (_gate)
            {
                if (_events.Count > 0)
                {
                    var head = _events[0];
                    _events.RemoveAt(0);
                    return head.Message;
                }
                arrival = _arrival;
            }

            var remaining = Remaining(timeout, Stopwatch.GetElapsedTime(start));
            if (Expired(remaining)) return null;
            var waited = WaitForArrival(sync: true, arrival, remaining, CancellationToken.None);
            Debug.Assert(waited.IsCompleted, "the sync path must complete synchronously; see WaitForArrival");
            if (!waited.GetAwaiter().GetResult()) return null;
        }
    }

    /// <summary>
    /// Wait for a single matching event. Shorthand for WaitEvents with finalEventCount=1.
    /// </summary>
    public BgapiEventList WaitEvent(
        EventSelector selector,
        TimeSpan timeout,
        Dictionary<string, object>? paramSubs = null,
        CancellationToken ct = default)
    {
        return WaitEvents(selector, timeout,
            maxEvents: 1,
            maxTime: timeout,
            finalEventCount: 1,
            paramSubs: paramSubs,
            ct: ct);
    }

    /// <summary>
    /// Wait for a single matching event without blocking a thread. Shorthand for WaitEventsAsync
    /// with finalEventCount=1.
    /// </summary>
    public Task<BgapiEventList> WaitEventAsync(
        EventSelector selector,
        TimeSpan timeout,
        Dictionary<string, object>? paramSubs = null,
        CancellationToken ct = default)
    {
        return WaitEventsAsync(selector, timeout,
            maxEvents: 1,
            maxTime: timeout,
            finalEventCount: 1,
            paramSubs: paramSubs,
            ct: ct);
    }

    /// <summary>
    /// Wait for events matching a selector. Ports Python BGLibExt.wait_events.
    ///
    /// Key differences from simple dequeue:
    /// - Non-matching events are left in the queue, in arrival order, for other waiters — never
    ///   held by this call, re-ordered, or dropped
    /// - Supports soft final event count for early termination
    /// - Supports event accumulation across calls via selectedEvents parameter
    /// - Throws BgapiWaitEventException when finalEventCount is specified but not reached
    /// - Throws OperationCanceledException when ct is cancelled, before or during the wait
    ///
    /// The wait ends at the earlier of <paramref name="timeout"/> and <paramref name="maxTime"/>,
    /// measured from the call (or from the last selected event when
    /// <paramref name="resetTimeoutOnEvent"/>), when the final count is reached, or when
    /// <paramref name="maxEvents"/> have been collected. <see cref="Timeout.InfiniteTimeSpan"/> for
    /// either bound means that bound is absent.
    /// </summary>
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
        var waited = WaitEventsCore(sync: true, null, selector, timeout, maxEvents, maxTime, resetTimeoutOnEvent, finalEventCount,
            softFinalEventCount, softFinalEventProcTime, keepAllEvents, paramSubs, selectedEvents, ct);
        Debug.Assert(waited.IsCompleted, "the sync path must complete synchronously; see WaitEventsCore");
        return waited.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Wait for events matching a selector without blocking a thread. Ports Python
    /// BGLibExt.wait_events, as <see cref="WaitEvents"/> does: same parameters, same stop
    /// conditions, same exceptions — only the wait for the next event is awaited instead of blocked.
    /// </summary>
    public Task<BgapiEventList> WaitEventsAsync(
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
        CancellationToken ct = default) =>
        WaitEventsCore(sync: false, null, selector, timeout, maxEvents, maxTime, resetTimeoutOnEvent, finalEventCount,
            softFinalEventCount, softFinalEventProcTime, keepAllEvents, paramSubs, selectedEvents, ct).AsTask();

    /// <summary>
    /// WaitEvents and WaitEventsAsync, also adding each event it counts toward the final count to
    /// <paramref name="counted"/>, so RetryUntilAsync can take back the count of one it drops.
    /// </summary>
    /// <remarks>
    /// <paramref name="sync"/> picks how the call waits for the next event, and nothing else.
    /// RetryUntilAsync used to call the blocking WaitEvents, so once its command completed
    /// synchronously it held the caller's thread for every event wait before it returned a task.
    /// The sync path is not a wrapper over the async one: <c>WaitEventsAsync(...).GetAwaiter().GetResult()</c>
    /// would need a second pool thread to resume on at every arrival while the caller's own thread
    /// sat blocked — the starvation the async path exists to avoid. So with <c>sync: true</c> the
    /// only wait is a blocking one, every await below completes synchronously, and the call runs
    /// start to finish on the caller's thread.
    /// </remarks>
    private async ValueTask<BgapiEventList> WaitEventsCore(
        bool sync,
        HashSet<BgapiMessage>? counted,
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
        if (softFinalEventCount.HasValue && finalEventCount.HasValue &&
            finalEventCount.Value < softFinalEventCount.Value)
        {
            throw new ArgumentException(
                $"softFinalEventCount ({softFinalEventCount}) must be <= finalEventCount ({finalEventCount})");
        }

        // paramSubs used to be accepted and never read, so a $name constraint kept its literal
        // "$name" string and RetryUntilAsync's waits, which pass the command response here, could
        // never match an event that carried the response's value.
        if (paramSubs is { Count: > 0 })
            selector = selector.WithParamSubs(paramSubs);

        var effectiveFinalCount = finalEventCount ?? int.MaxValue;
        var thresholdCount = softFinalEventCount ?? effectiveFinalCount;
        var selected = selectedEvents ?? new BgapiEventList();
        var effectiveMaxTime = maxTime ?? TimeSpan.FromSeconds(_config.WaitEventsDefaultMaxTimeSeconds);
        var budget = Earlier(timeout, effectiveMaxTime);

        // A receive loop calls this every few hundred milliseconds. With Debug off it used to pay for
        // the params array and boxed numbers LogDebug builds before the logger is asked.
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("WaitEvents: selector={Selector} timeout={Timeout}ms maxTime={MaxTime}ms finalEventCount={FinalEventCount}",
                selector, timeout.TotalMilliseconds, effectiveMaxTime.TotalMilliseconds, finalEventCount);

        // Every queued event with Seq > cursor is still unexamined by THIS call. Each call examines
        // each event at most once and removes only what its selector takes; what it ignores stays
        // in place for other waiters, so nothing is ever held, re-ordered or lost on cancellation.
        long cursor = -1;
        long started = Stopwatch.GetTimestamp();
        bool thresholdReached = false;
        bool maxEventsReached = false;
        bool woken = false;

        while (true)
        {
            // Cancellation and the deadline are checked between candidates, so a backlog, a steady
            // stream of ignored events, or a slow selector cannot carry the call past its budget.
            // Past the deadline, one candidate is still examined after a wake, if it arrived before
            // the deadline: an awaiting waiter resumes only once a pool thread is free. A completed
            // ticket alone is no proof — a waiter paused before its wait finds the ticket completed
            // by whatever came while it was paused.
            ct.ThrowIfCancellationRequested();
            var remaining = Remaining(budget, Stopwatch.GetElapsedTime(started));
            var expired = Expired(remaining);
            if (expired && !woken) break;
            woken = false;
            var arrivedBy = expired ? started + (long)(budget.TotalSeconds * Stopwatch.Frequency) : long.MaxValue;

            var scan = ScanNext(selector, selected, keepAllEvents, ref cursor, out var message, out var category, out var arrival, arrivedBy);
            if (scan == Scan.Skipped) continue;
            if (scan == Scan.Exhausted)
            {
                if (expired || !await WaitForArrival(sync, arrival!, remaining, ct).ConfigureAwait(false)) break;
                woken = true;
                continue;
            }

            selected.Add(message!);
            if (category == EventCategory.SelectFinal)
            {
                selected.FinalEventCount++;
                counted?.Add(message!);
                if (selected.FinalEventCount >= thresholdCount)
                {
                    thresholdReached = true;
                    break;
                }
            }

            if (category != EventCategory.Ignore && resetTimeoutOnEvent)
                started = Stopwatch.GetTimestamp();

            if (selected.Count >= maxEvents)
            {
                maxEventsReached = true;
                break;
            }
        }

        if (thresholdReached && thresholdCount < effectiveFinalCount)
        {
            // Soft final reached: keep collecting for a short window, up to the hard final count.
            if (softFinalEventProcTime.HasValue)
            {
                var window = Stopwatch.StartNew();
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var remaining = Remaining(softFinalEventProcTime.Value, window.Elapsed);
                    if (Expired(remaining)) break;

                    var scan = ScanNext(selector, selected, keepAllEvents, ref cursor, out var message, out var category, out var arrival);
                    if (scan == Scan.Skipped) continue;
                    if (scan == Scan.Exhausted)
                    {
                        if (!await WaitForArrival(sync, arrival!, remaining, ct).ConfigureAwait(false)) break;
                        continue;
                    }

                    selected.Add(message!);
                    if (category == EventCategory.SelectFinal)
                    {
                        selected.FinalEventCount++;
                        counted?.Add(message!);
                        if (selected.FinalEventCount >= effectiveFinalCount)
                            break;
                    }
                }
            }

            return selected;
        }

        // finalEventCount specified but not reached: throw — unless maxEvents stopped the wait first.
        if (!maxEventsReached && finalEventCount.HasValue && selected.FinalEventCount < thresholdCount)
            throw new BgapiWaitEventException(selector, selected);

        return selected;
    }

    private enum Scan
    {
        /// <summary>The event was selected (or <c>takeIgnored</c>) and removed from the queue; <c>message</c> is set.</summary>
        Taken,
        /// <summary>The event stays for other waiters — the selector ignored it, or another waiter took it first.</summary>
        Skipped,
        /// <summary>Nothing past the cursor, arrived by <c>arrivedBy</c>, is left to examine; <c>arrival</c> is the ticket to wait on.</summary>
        Exhausted,
    }

    /// <summary>
    /// Examines the one queued event past <paramref name="cursor"/>, advancing the cursor past it.
    /// One candidate per call, so the caller can check its deadline and token between candidates.
    /// An event enqueued after the Stopwatch timestamp <paramref name="arrivedBy"/> is not examined.
    /// </summary>
    private Scan ScanNext(
        EventSelector selector,
        BgapiEventList selected,
        bool takeIgnored,
        ref long cursor,
        out BgapiMessage? message,
        out EventCategory category,
        out TaskCompletionSource<bool>? arrival,
        long arrivedBy = long.MaxValue)
    {
        QueuedEvent candidate;
        lock (_gate)
        {
            int i = IndexOfFirstAfter(cursor);
            if (i >= _events.Count || _events[i].ArrivedAt > arrivedBy)
            {
                message = null;
                category = EventCategory.Ignore;
                arrival = _arrival;
                return Scan.Exhausted;
            }
            candidate = _events[i];
        }

        cursor = candidate.Seq;
        message = null;
        arrival = null;

        // Selector code runs with the lock released: a slow CallableSelector must never stall
        // the reader thread's Enqueue.
        category = selector.Categorize(candidate.Message, selected);
        if (category == EventCategory.Ignore && !takeIgnored)
            return Scan.Skipped;

        lock (_gate)
        {
            // Another waiter may have taken it since the scan; then it was never ours.
            if (!RemoveBySeq(candidate.Seq))
                return Scan.Skipped;
        }

        message = candidate.Message;
        return Scan.Taken;
    }

    /// <summary>Index of the first queued event with Seq greater than <paramref name="seq"/>. Caller holds the lock.</summary>
    private int IndexOfFirstAfter(long seq)
    {
        int lo = 0, hi = _events.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (_events[mid].Seq <= seq) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    /// <summary>Removes the event with exactly this Seq, if still queued. Caller holds the lock.</summary>
    private bool RemoveBySeq(long seq)
    {
        int i = IndexOfFirstAfter(seq - 1);
        if (i < _events.Count && _events[i].Seq == seq)
        {
            _events.RemoveAt(i);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Waits until the ticket completes (an event was enqueued), the remaining time runs out
    /// (returns false), or <paramref name="ct"/> is cancelled (throws OperationCanceledException).
    /// <see cref="Timeout.InfiniteTimeSpan"/> waits without a bound. With <paramref name="sync"/>
    /// it blocks the calling thread and completes synchronously; otherwise it awaits.
    /// </summary>
    private async ValueTask<bool> WaitForArrival(bool sync, TaskCompletionSource<bool> arrival, TimeSpan remaining, CancellationToken ct)
    {
        var ms = remaining == Timeout.InfiniteTimeSpan
            ? Timeout.Infinite
            : (int)Math.Min(int.MaxValue, Math.Ceiling(remaining.TotalMilliseconds));
        BeforeArrival?.Invoke();
        bool signaled;
        if (sync)
        {
            signaled = arrival.Task.Wait(ms, ct);
        }
        else
        {
            // WaitAsync reports a window that ran out by throwing TimeoutException, so every wait
            // that finds nothing would end in one — a receive loop polling on a short timeout would
            // throw and catch one per poll. Suppressed here, the outcome is read off the ticket, and
            // cancellation is rethrown as the same OperationCanceledException the blocking wait throws.
            await ((Task)arrival.Task.WaitAsync(TimeSpan.FromMilliseconds(ms), ct))
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            ct.ThrowIfCancellationRequested();
            signaled = arrival.Task.IsCompleted;
        }
        if (signaled) AfterArrival?.Invoke();
        return signaled;
    }

    /// <summary>The earlier of two bounds; <see cref="Timeout.InfiniteTimeSpan"/> counts as no bound.</summary>
    private static TimeSpan Earlier(TimeSpan a, TimeSpan b)
    {
        if (a == Timeout.InfiniteTimeSpan) return b;
        if (b == Timeout.InfiniteTimeSpan) return a;
        return a < b ? a : b;
    }

    /// <summary>
    /// Time left of <paramref name="budget"/>, never below zero: an overrun of exactly 1 ms would
    /// otherwise equal <see cref="Timeout.InfiniteTimeSpan"/> and read as no bound at all.
    /// </summary>
    internal static TimeSpan Remaining(TimeSpan budget, TimeSpan elapsed) =>
        budget == Timeout.InfiniteTimeSpan ? budget
            : elapsed >= budget ? TimeSpan.Zero
            : budget - elapsed;

    internal static bool Expired(TimeSpan remaining) =>
        remaining != Timeout.InfiniteTimeSpan && remaining <= TimeSpan.Zero;

    /// <summary>
    /// Retry a command until expected events are received. Ports Python BGLibExt.retry_until.
    ///
    /// Key features:
    /// - Builds paramSubs from command response for event selector $param substitution
    /// - Rebuilds event selector after each successful command
    /// - Supports retry event selector for recoverable error detection
    /// - Accumulates events across retry iterations
    /// - If eventSelector is null, returns an empty BgapiEventList as soon as the command succeeds
    ///   (no event waiting)
    /// </summary>
    /// <remarks>
    /// The first call to <paramref name="command"/> runs on the caller's thread. Later attempts, and
    /// selectors examined while waiting for events, may run on a thread-pool thread: no await here
    /// resumes on the caller's SynchronizationContext, so a caller blocked on this task on a UI thread
    /// still gets it back.
    /// </remarks>
    public async Task<BgapiEventList> RetryUntilAsync(
        Func<Task<BgapiCommandResponse>> command,
        EventSelector? eventSelector,
        RetryParams retryParams,
        IReadOnlyList<SlStatus>? retryCmdErrorCodes = null,
        EventSelector? retryEventSelector = null,
        int finalEventCount = 1,
        int? softFinalEventCount = null,
        TimeSpan? softFinalEventProcTime = null,
        bool keepAllEvents = false,
        bool retryIntRstOnEvt = false,
        CancellationToken ct = default)
    {
        var retryErrorCodes = retryCmdErrorCodes ?? [SlStatus.Busy, SlStatus.NoMoreResource];
        var selectedEvents = new BgapiEventList();
        // The events selectedEvents counted toward finalEventCount, so one dropped before a retry
        // gives back its count. Kept here rather than on the list the caller is handed: a caller
        // reusing that list and clearing it kept every event the list had counted alive.
        var counted = new HashSet<BgapiMessage>();
        bool anyCmdSuccess = false;
        bool lastCmdFailed = false;
        int prevSelEvtCount = 0;
        // Kept across outer iterations: when a later attempt's command fails transiently, the
        // wait in its place collects events from the last command that succeeded, so it resolves
        // $name against that command's response. Declared per iteration, that wait had none.
        Dictionary<string, object> paramSubs = [];

        _logger.LogDebug("RetryUntilAsync: retryMax={RetryMax} retryCmdMax={RetryCmdMax} finalEventCount={FinalEventCount}",
            retryParams.RetryMax, retryParams.RetryCmdMax, finalEventCount);

        for (int retryCount = 0; retryCount <= retryParams.RetryMax; retryCount++)
        {
            ct.ThrowIfCancellationRequested();

            // Execute command with retries for transient errors
            BgapiCommandResponse? cmdResponse = null;

            for (int cmdAttempt = 0; cmdAttempt <= retryParams.RetryCmdMax; cmdAttempt++)
            {
                try
                {
                    cmdResponse = await command().ConfigureAwait(false);

                    if (cmdResponse.Status != SlStatus.OK)
                    {
                        if (!retryErrorCodes.Contains(cmdResponse.Status))
                            throw new BgapiCommandException(cmdResponse.Status);

                        lastCmdFailed = true;

                        if (anyCmdSuccess)
                        {
                            // Wait for events during command retry timeout
                            prevSelEvtCount = selectedEvents.Count;
                            try
                            {
                                selectedEvents = await WaitEventsCore(
                                    sync: false,
                                    counted,
                                    eventSelector!,
                                    retryParams.RetryCmdInterval,
                                    resetTimeoutOnEvent: retryIntRstOnEvt,
                                    finalEventCount: finalEventCount,
                                    softFinalEventCount: softFinalEventCount,
                                    softFinalEventProcTime: softFinalEventProcTime,
                                    keepAllEvents: keepAllEvents,
                                    paramSubs: paramSubs,
                                    selectedEvents: selectedEvents,
                                    ct: ct).ConfigureAwait(false);
                                goto ReturnEvents;
                            }
                            catch (BgapiWaitEventException)
                            {
                                _logger.LogDebug("Command retry {CmdAttempt}/{RetryCmdMax}: events not yet complete, retrying command",
                                    cmdAttempt, retryParams.RetryCmdMax);
                                continue;
                            }
                        }
                        else
                        {
                            // Wait before retrying
                            if (cmdAttempt < retryParams.RetryCmdMax)
                                await Task.Delay(retryParams.RetryCmdInterval, ct).ConfigureAwait(false);
                            continue;
                        }
                    }

                    // Command succeeded
                    if (eventSelector is null)
                    {
                        // No events expected, return immediately
                        return new BgapiEventList();
                    }

                    paramSubs = cmdResponse.BuildParamSubs();

                    // Reset selected events for stateless selectors after successful command
                    if (eventSelector.Stateless)
                    {
                        selectedEvents = new BgapiEventList();
                        counted.Clear();
                    }

                    anyCmdSuccess = true;
                    lastCmdFailed = false;
                    break;
                }
                catch (BgapiCommandException)
                {
                    lastCmdFailed = true;
                    throw;
                }
            }

            if (cmdResponse is null || cmdResponse.Status != SlStatus.OK)
            {
                if (lastCmdFailed)
                    throw new BgapiCommandException(cmdResponse?.Status ?? SlStatus.Fail);
            }

            if (!lastCmdFailed)
            {
                // Resolved like the event selector: matched as written, a "$name" constraint
                // on the failure event never matched, and a failure was returned as success.
                var retrySelector = retryEventSelector is not null && paramSubs.Count > 0
                    ? retryEventSelector.WithParamSubs(paramSubs)
                    : retryEventSelector;

                // Wait for events
                prevSelEvtCount = selectedEvents.Count;
                try
                {
                    selectedEvents = await WaitEventsCore(
                        sync: false,
                        counted,
                        eventSelector!,
                        retryParams.RetryInterval,
                        resetTimeoutOnEvent: retryIntRstOnEvt,
                        finalEventCount: finalEventCount,
                        softFinalEventCount: softFinalEventCount,
                        softFinalEventProcTime: softFinalEventProcTime,
                        keepAllEvents: keepAllEvents,
                        paramSubs: paramSubs,
                        selectedEvents: selectedEvents,
                        ct: ct).ConfigureAwait(false);
                }
                catch (BgapiWaitEventException ex)
                {
                    if (retryCount < retryParams.RetryMax)
                    {
                        // WaitEvents collected into selectedEvents in place, so the next attempt
                        // carries on from it with its final count. It used to restart from a copy
                        // whose count was 0, and a stateful selector, which keeps the events it
                        // already counted, could then never reach finalEventCount.
                        // Failures the retry event selector matches are dropped first, each with
                        // the count it took. Kept, as in Python, a failure was never checked, since
                        // only a wait that completes is: it counted toward finalEventCount, under
                        // #unique it hid its node's later success, and a failed node could
                        // complete the result.
                        _logger.LogDebug("Retry {RetryCount}/{RetryMax}: events not complete ({Collected} collected), retrying",
                            retryCount, retryParams.RetryMax, ex.CollectedEvents.Count);
                        if (retrySelector is not null)
                            Drop(selectedEvents, counted, e => retrySelector.Categorize(e, []) != EventCategory.Ignore);
                        continue;
                    }
                    _logger.LogWarning("RetryUntilAsync exhausted all {RetryMax} retries", retryParams.RetryMax);
                    throw;
                }

                // Check retry event selector for recoverable errors
                var newEvents = selectedEvents.Skip(prevSelEvtCount).ToList();
                if (retrySelector is not null && retryCount < retryParams.RetryMax)
                {
                    bool hasRetryEvent = newEvents.Any(e =>
                        retrySelector.Categorize(e, []) != EventCategory.Ignore);

                    if (hasRetryEvent)
                    {
                        // A stateful selector's events used to be kept whole, failures included,
                        // and what they had counted could end the next wait at once. Dropped as
                        // after a wait that falls short.
                        if (eventSelector!.Stateless)
                        {
                            selectedEvents = new BgapiEventList();
                            counted.Clear();
                        }
                        else
                            Drop(selectedEvents, counted, e => retrySelector.Categorize(e, []) != EventCategory.Ignore);
                        continue;
                    }
                }

                // Success
                goto ReturnEvents;
            }

            if (retryCount < retryParams.RetryMax)
                await Task.Delay(retryParams.RetryInterval, ct).ConfigureAwait(false);
        }

        // Max retries exhausted
        _logger.LogWarning("RetryUntilAsync exhausted all {RetryMax} retries", retryParams.RetryMax);
        throw new BgapiWaitEventException(eventSelector!, selectedEvents);

    ReturnEvents:
        return selectedEvents;
    }

    /// <summary>Removes the events <paramref name="match"/> selects, taking back the count of each one counted.</summary>
    private static void Drop(BgapiEventList events, HashSet<BgapiMessage> counted, Predicate<BgapiMessage> match)
    {
        foreach (var message in events.FindAll(match))
        {
            events.Remove(message);
            if (counted.Remove(message))
                events.FinalEventCount--;
        }
    }

    /// <summary>Drops every retained event and starts a new drop-warning episode.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _events.Clear();
            _dropLogged = false;
        }
    }
}

public record RetryParams
{
    // These literals must stay equal to the CsharpBgapiOptions defaults: a caller who hand-builds
    // a RetryParams to set one field takes them for every field left alone. RetryCmdMax used to
    // read 10 against the option's 6, so such a caller silently got a retry budget nobody
    // configured. Pinned by RetryParamsTests.
    public int RetryMax { get; init; } = 5;
    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromSeconds(1);
    public int RetryCmdMax { get; init; } = 6;
    public TimeSpan RetryCmdInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Create RetryParams from CsharpBgapiOptions defaults.
    /// </summary>
    public static RetryParams FromOptions(CsharpBgapiOptions options) => new()
    {
        RetryMax = options.RetryMax,
        RetryInterval = TimeSpan.FromSeconds(options.RetryIntervalSeconds),
        RetryCmdMax = options.RetryCmdMax,
        RetryCmdInterval = TimeSpan.FromSeconds(options.RetryCmdIntervalSeconds),
    };
}

public class BgapiCommandException : Exception
{
    public SlStatus Status { get; }

    public BgapiCommandException(SlStatus status)
        : base($"BGAPI command failed with status: {status}")
    {
        Status = status;
    }
}

public class BgapiWaitEventException : Exception
{
    public EventSelector Selector { get; }
    public IReadOnlyList<BgapiMessage> CollectedEvents { get; }

    public BgapiWaitEventException(EventSelector selector, IReadOnlyList<BgapiMessage> collectedEvents)
        : base("Timed out waiting for expected events")
    {
        Selector = selector;
        CollectedEvents = collectedEvents;
    }
}
