using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SilabsBgapi.Protocol;

namespace SilabsBgapi.Events;

/// <summary>
/// Event queue with selector-based waiting. Ports BGLibExt event waiting patterns.
/// Events are enqueued from the reader thread and consumed by WaitEvents/RetryUntilAsync.
/// </summary>
public sealed class BgapiEventQueue
{
    private readonly SilabsBgapiOptions _config;
    private readonly ILogger _logger;
    private readonly ConcurrentQueue<BgapiMessage> _queue = new();
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);

    public BgapiEventQueue() : this((ILogger?)null) { }

    public BgapiEventQueue(ILogger? logger)
        : this(Options.Create(new SilabsBgapiOptions()), logger ?? NullLogger.Instance) { }

    public BgapiEventQueue(IOptions<SilabsBgapiOptions> options, ILogger<BgapiEventQueue> logger)
        : this(options, (ILogger)logger) { }

    internal BgapiEventQueue(IOptions<SilabsBgapiOptions> options, ILogger logger)
    {
        _config = options.Value;
        _logger = logger;
    }

    public void Enqueue(BgapiMessage message)
    {
        _queue.Enqueue(message);
        _signal.Release();
    }

    /// <summary>
    /// Gets the next event from the queue, waiting up to timeout.
    /// </summary>
    public BgapiMessage? GetEvent(TimeSpan timeout)
    {
        if (_signal.Wait(timeout) && _queue.TryDequeue(out var msg))
            return msg;
        return null;
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
    /// Wait for events matching a selector. Ports Python BGLibExt.wait_events.
    ///
    /// Key differences from simple dequeue:
    /// - Non-matching events are re-enqueued (not dropped)
    /// - Supports soft final event count for early termination
    /// - Supports event accumulation across calls via selectedEvents parameter
    /// - Throws BgapiWaitEventException when finalEventCount is specified but not reached
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
        if (softFinalEventCount.HasValue && finalEventCount.HasValue &&
            finalEventCount.Value < softFinalEventCount.Value)
        {
            throw new ArgumentException(
                $"softFinalEventCount ({softFinalEventCount}) must be <= finalEventCount ({finalEventCount})");
        }

        var effectiveFinalCount = finalEventCount ?? int.MaxValue;
        var thresholdCount = softFinalEventCount ?? effectiveFinalCount;
        var selected = selectedEvents ?? new BgapiEventList();
        var effectiveMaxTime = maxTime ?? TimeSpan.FromSeconds(_config.WaitEventsDefaultMaxTimeSeconds);
        var ignoredEvents = new List<BgapiMessage>();

        _logger.LogDebug("WaitEvents: selector={Selector} timeout={Timeout}ms maxTime={MaxTime}ms finalEventCount={FinalEventCount}",
            selector, timeout.TotalMilliseconds, effectiveMaxTime.TotalMilliseconds, finalEventCount);

        bool restartMaxTime = true;
        while (restartMaxTime && !ct.IsCancellationRequested)
        {
            restartMaxTime = false;
            var sw = Stopwatch.StartNew();

            while (!ct.IsCancellationRequested)
            {
                if (sw.Elapsed >= effectiveMaxTime)
                    break;

                var remaining = timeout - (resetTimeoutOnEvent ? TimeSpan.Zero : sw.Elapsed);
                if (remaining <= TimeSpan.Zero)
                    remaining = TimeSpan.FromMilliseconds(1);

                var waitTime = effectiveMaxTime - sw.Elapsed;
                if (waitTime <= TimeSpan.Zero) break;
                if (waitTime > remaining) waitTime = remaining;

                if (!_signal.Wait(waitTime, ct))
                    break;

                if (!_queue.TryDequeue(out var message))
                    continue;

                var category = selector.Categorize(message, selected);
                switch (category)
                {
                    case EventCategory.SelectFinal:
                        selected.Add(message);
                        selected.FinalEventCount++;
                        if (selected.FinalEventCount >= thresholdCount)
                            goto ThresholdReached;
                        if (resetTimeoutOnEvent)
                        {
                            restartMaxTime = true;
                            goto BreakInner;
                        }
                        break;

                    case EventCategory.SelectContinue:
                        selected.Add(message);
                        if (resetTimeoutOnEvent)
                        {
                            restartMaxTime = true;
                            goto BreakInner;
                        }
                        break;

                    case EventCategory.Ignore:
                        if (keepAllEvents)
                            selected.Add(message);
                        else
                            ignoredEvents.Add(message);
                        continue;
                }

                if (selected.Count >= maxEvents)
                    goto Done;
            }
            BreakInner:;
        }

    ThresholdReached:
        if (selected.FinalEventCount >= thresholdCount && thresholdCount < effectiveFinalCount)
        {
            // Soft final reached: process remaining events for a short time
            if (softFinalEventProcTime.HasValue)
            {
                var procSw = Stopwatch.StartNew();
                while (procSw.Elapsed < softFinalEventProcTime.Value && !ct.IsCancellationRequested)
                {
                    var remaining = softFinalEventProcTime.Value - procSw.Elapsed;
                    if (remaining <= TimeSpan.Zero) break;

                    if (!_signal.Wait(remaining, ct))
                        break;

                    if (!_queue.TryDequeue(out var message))
                        continue;

                    var category = selector.Categorize(message, selected);
                    if (category == EventCategory.SelectContinue)
                    {
                        selected.Add(message);
                    }
                    else if (category == EventCategory.SelectFinal)
                    {
                        selected.Add(message);
                        selected.FinalEventCount++;
                        if (selected.FinalEventCount >= effectiveFinalCount)
                            break;
                    }
                    else if (keepAllEvents)
                    {
                        selected.Add(message);
                    }
                    else
                    {
                        ignoredEvents.Add(message);
                    }
                }
            }

            ReEnqueueIgnored(ignoredEvents);
            return selected;
        }

        // If finalEventCount was specified but not reached, throw
        if (finalEventCount.HasValue && selected.FinalEventCount < thresholdCount)
        {
            ReEnqueueIgnored(ignoredEvents);
            throw new BgapiWaitEventException(selector, selected);
        }

    Done:
        ReEnqueueIgnored(ignoredEvents);
        return selected;
    }

    /// <summary>
    /// Retry a command until expected events are received. Ports Python BGLibExt.retry_until.
    ///
    /// Key features:
    /// - Builds paramSubs from command response for event selector $param substitution
    /// - Rebuilds event selector after each successful command
    /// - Supports retry event selector for recoverable error detection
    /// - Accumulates events across retry iterations
    /// - If eventSelector is null, returns command response only (no event waiting)
    /// </summary>
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
        bool anyCmdSuccess = false;
        bool lastCmdFailed = false;
        int prevSelEvtCount = 0;

        _logger.LogDebug("RetryUntilAsync: retryMax={RetryMax} retryCmdMax={RetryCmdMax} finalEventCount={FinalEventCount}",
            retryParams.RetryMax, retryParams.RetryCmdMax, finalEventCount);

        for (int retryCount = 0; retryCount <= retryParams.RetryMax; retryCount++)
        {
            ct.ThrowIfCancellationRequested();

            // Execute command with retries for transient errors
            BgapiCommandResponse? cmdResponse = null;
            Dictionary<string, object> paramSubs = [];

            for (int cmdAttempt = 0; cmdAttempt <= retryParams.RetryCmdMax; cmdAttempt++)
            {
                try
                {
                    cmdResponse = await command();

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
                                selectedEvents = WaitEvents(
                                    eventSelector!,
                                    retryParams.RetryCmdInterval,
                                    resetTimeoutOnEvent: retryIntRstOnEvt,
                                    finalEventCount: finalEventCount,
                                    softFinalEventCount: softFinalEventCount,
                                    softFinalEventProcTime: softFinalEventProcTime,
                                    keepAllEvents: keepAllEvents,
                                    paramSubs: paramSubs,
                                    selectedEvents: selectedEvents,
                                    ct: ct);
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
                                await Task.Delay(retryParams.RetryCmdInterval, ct);
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
                        selectedEvents = new BgapiEventList();

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
                // Wait for events
                prevSelEvtCount = selectedEvents.Count;
                try
                {
                    selectedEvents = WaitEvents(
                        eventSelector!,
                        retryParams.RetryInterval,
                        resetTimeoutOnEvent: retryIntRstOnEvt,
                        finalEventCount: finalEventCount,
                        softFinalEventCount: softFinalEventCount,
                        softFinalEventProcTime: softFinalEventProcTime,
                        keepAllEvents: keepAllEvents,
                        paramSubs: paramSubs,
                        selectedEvents: selectedEvents,
                        ct: ct);
                }
                catch (BgapiWaitEventException ex)
                {
                    if (retryCount < retryParams.RetryMax)
                    {
                        _logger.LogDebug("Retry {RetryCount}/{RetryMax}: events not complete ({Collected} collected), retrying",
                            retryCount, retryParams.RetryMax, ex.CollectedEvents.Count);
                        selectedEvents = new BgapiEventList(ex.CollectedEvents);
                        continue;
                    }
                    _logger.LogWarning("RetryUntilAsync exhausted all {RetryMax} retries", retryParams.RetryMax);
                    throw;
                }

                // Check retry event selector for recoverable errors
                var newEvents = selectedEvents.Skip(prevSelEvtCount).ToList();
                if (retryEventSelector is not null && retryCount < retryParams.RetryMax)
                {
                    bool hasRetryEvent = newEvents.Any(e =>
                        retryEventSelector.Categorize(e, []) != EventCategory.Ignore);

                    if (hasRetryEvent)
                    {
                        if (eventSelector!.Stateless)
                            selectedEvents = new BgapiEventList();
                        continue;
                    }
                }

                // Success
                goto ReturnEvents;
            }

            if (retryCount < retryParams.RetryMax)
                await Task.Delay(retryParams.RetryInterval, ct);
        }

        // Max retries exhausted
        _logger.LogWarning("RetryUntilAsync exhausted all {RetryMax} retries", retryParams.RetryMax);
        throw new BgapiWaitEventException(eventSelector!, selectedEvents);

    ReturnEvents:
        return selectedEvents;
    }

    /// <summary>
    /// Re-enqueue ignored events so they aren't lost.
    /// </summary>
    private void ReEnqueueIgnored(List<BgapiMessage> ignoredEvents)
    {
        foreach (var evt in ignoredEvents)
        {
            _queue.Enqueue(evt);
            _signal.Release();
        }
    }

    public void Clear()
    {
        while (_queue.TryDequeue(out _)) { }
    }
}

public record RetryParams
{
    public int RetryMax { get; init; } = 5;
    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromSeconds(1);
    public int RetryCmdMax { get; init; } = 10;
    public TimeSpan RetryCmdInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Create RetryParams from SilabsBgapiOptions defaults.
    /// </summary>
    public static RetryParams FromOptions(SilabsBgapiOptions options) => new()
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
