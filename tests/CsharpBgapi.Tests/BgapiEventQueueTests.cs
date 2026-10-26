using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using CsharpBgapi.Events;
using CsharpBgapi.Protocol;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CsharpBgapi.Tests;

public class BgapiEventQueueTests
{
    // A wait for one event must leave every other event where it is. WaitEvents used to dequeue
    // EVERYTHING, hold the non-matching events in a private list, and re-enqueue them at the tail
    // only when the call returned. With two consumers on one queue — the Hub's vendor-model
    // receive loop polling every 200 ms and its firmware clients waiting up to a chunk budget —
    // a firmware wait kept the vendor-model events it won until its own window ended, order was
    // lost on re-enqueue, a wait cancelled mid-flight threw straight out and dropped what it held,
    // and events no consumer ever selected were re-queued forever and re-examined by every later
    // call. Now each waiter scans in place from its own cursor and removes only what it takes;
    // unclaimed events are bounded by MaxQueuedEvents.

    private const string VendorEvent = "btmesh_evt_vendor_model_receive";
    private const string BlockStatusEvent = "btmesh_evt_mbt_client_query_block_status_complete";

    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(1);

    private static BgapiMessage Event(string name)
    {
        var header = new BgapiHeader((byte)(BgapiHeader.EventFlag | (5 << 3)), payloadLength: 0, classIndex: 0, commandIndex: 0);
        return new BgapiMessage(header, ReadOnlyMemory<byte>.Empty) { EventName = name };
    }

    private static NameParamSelector Select(string name) => new(name);

    private static BgapiEventQueue QueueWithLimit(int maxQueuedEvents) =>
        new(Options.Create(new CsharpBgapiOptions { MaxQueuedEvents = maxQueuedEvents }), NullLogger<BgapiEventQueue>.Instance);

    [Fact]
    public void WaitEvents_NonMatchingEvents_StayQueuedInArrivalOrder()
    {
        var queue = new BgapiEventQueue();
        var vendor1 = Event(VendorEvent);
        var blockStatus = Event(BlockStatusEvent);
        var vendor2 = Event(VendorEvent);
        queue.Enqueue(vendor1);
        queue.Enqueue(blockStatus);
        queue.Enqueue(vendor2);

        var taken = queue.WaitEvents(Select(BlockStatusEvent), Short, maxTime: Short);

        taken.Should().ContainSingle().Which.Should().BeSameAs(blockStatus);
        queue.Count.Should().Be(2);
        queue.GetEvent(TimeSpan.Zero).Should().BeSameAs(vendor1);
        queue.GetEvent(TimeSpan.Zero).Should().BeSameAs(vendor2);
    }

    [Fact]
    public void WaitEvents_EventQueuedBeforeTheCall_IsFound()
    {
        var queue = new BgapiEventQueue();
        var blockStatus = Event(BlockStatusEvent);
        queue.Enqueue(blockStatus);

        var sw = Stopwatch.StartNew();
        var taken = queue.WaitEvents(Select(BlockStatusEvent), Long, maxTime: Long);

        taken.Should().ContainSingle().Which.Should().BeSameAs(blockStatus);
        taken.FinalEventCount.Should().Be(1);
        sw.Elapsed.Should().BeLessThan(Prompt);
        queue.Count.Should().Be(0);
    }

    [Fact]
    public void WaitEvents_EventArrivingDuringTheWait_IsDelivered()
    {
        var queue = new BgapiEventQueue();
        var blockStatus = Event(BlockStatusEvent);
        var producer = new Thread(() =>
        {
            Thread.Sleep(Short);
            queue.Enqueue(blockStatus);
        });
        producer.Start();

        var sw = Stopwatch.StartNew();
        var taken = queue.WaitEvents(Select(BlockStatusEvent), Long, maxTime: Long);

        taken.Should().ContainSingle().Which.Should().BeSameAs(blockStatus);
        sw.Elapsed.Should().BeLessThan(Prompt);
        producer.Join(Long).Should().BeTrue();
    }

    [Fact]
    public void WaitEvents_ConcurrentWaiterForAnotherEvent_DoesNotDelayDelivery()
    {
        // The Hub shape: a firmware wait sits on the queue for seconds while the receive loop
        // wants its vendor-model events now. The firmware wait must not take them.
        var queue = new BgapiEventQueue();
        var firmwareWait = new Thread(() =>
        {
            try { queue.WaitEvents(Select(BlockStatusEvent), Long, maxTime: Long); }
            catch (BgapiWaitEventException) { }
        });
        firmwareWait.Start();
        Thread.Sleep(Short);

        var vendor = Event(VendorEvent);
        queue.Enqueue(vendor);

        var sw = Stopwatch.StartNew();
        var taken = queue.WaitEvents(Select(VendorEvent), Long, maxTime: Long);

        taken.Should().ContainSingle().Which.Should().BeSameAs(vendor);
        sw.Elapsed.Should().BeLessThan(Prompt);

        queue.Enqueue(Event(BlockStatusEvent));
        firmwareWait.Join(Long).Should().BeTrue();
    }

    [Fact]
    public void WaitEvents_TwoWaitersSameSelector_EachEventDeliveredOnce()
    {
        var queue = new BgapiEventQueue();
        var results = new ConcurrentBag<BgapiMessage>();
        var waiters = new[]
        {
            new Thread(() => results.Add(queue.WaitEvents(Select(VendorEvent), Long, maxTime: Long).Single())),
            new Thread(() => results.Add(queue.WaitEvents(Select(VendorEvent), Long, maxTime: Long).Single())),
        };
        foreach (var waiter in waiters) waiter.Start();
        Thread.Sleep(Short);

        queue.Enqueue(Event(VendorEvent));
        queue.Enqueue(Event(VendorEvent));

        foreach (var waiter in waiters) waiter.Join(Long).Should().BeTrue();
        results.Should().HaveCount(2);
        results.Distinct().Should().HaveCount(2, "no event may be delivered to both waiters");
        queue.Count.Should().Be(0);
    }

    [Fact]
    public void WaitEvents_CancelledMidWait_ThrowsAndLeavesExaminedEventsQueued()
    {
        var queue = new BgapiEventQueue();
        var vendor = Event(VendorEvent);
        queue.Enqueue(vendor);
        using var cts = new CancellationTokenSource(Short);

        var act = () => queue.WaitEvents(Select(BlockStatusEvent), Long, maxTime: Long, ct: cts.Token);

        act.Should().Throw<OperationCanceledException>();
        queue.Count.Should().Be(1, "an event the cancelled wait examined and ignored must still be there");
        queue.GetEvent(TimeSpan.Zero).Should().BeSameAs(vendor);
    }

    [Fact]
    public void WaitEvents_TokenAlreadyCancelled_ThrowsOperationCanceled()
    {
        var queue = new BgapiEventQueue();
        queue.Enqueue(Event(VendorEvent));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => queue.WaitEvents(Select(VendorEvent), Long, maxTime: Long, ct: cts.Token);

        act.Should().Throw<OperationCanceledException>();
        queue.Count.Should().Be(1);
    }

    [Fact]
    public void WaitEvents_FinalCountNotReached_ThrowsWithCollectedEvents()
    {
        var queue = new BgapiEventQueue();
        var vendor = Event(VendorEvent);
        queue.Enqueue(vendor);
        var continueOnly = new NameParamSelector(VendorEvent, isFinal: false);

        var act = () => queue.WaitEvents(continueOnly, Short, maxTime: Short, finalEventCount: 1);

        act.Should().Throw<BgapiWaitEventException>()
            .Which.CollectedEvents.Should().ContainSingle().Which.Should().BeSameAs(vendor);
        queue.Count.Should().Be(0, "a selected event is taken even when the wait then fails");
    }

    [Fact]
    public void WaitEvents_FinalCountNull_ReturnsWhatMatchedByTheDeadline()
    {
        var queue = new BgapiEventQueue();

        var nothing = queue.WaitEvents(Select(VendorEvent), Short, maxTime: Short, finalEventCount: null);

        nothing.Should().BeEmpty();

        var vendor = Event(VendorEvent);
        queue.Enqueue(vendor);

        var taken = queue.WaitEvents(Select(VendorEvent), Short, maxTime: Short, finalEventCount: null);

        taken.Should().ContainSingle().Which.Should().BeSameAs(vendor);
    }

    [Fact]
    public void WaitEvents_MaxEvents_StopsAtTheLimit()
    {
        var queue = new BgapiEventQueue();
        queue.Enqueue(Event(VendorEvent));
        queue.Enqueue(Event(VendorEvent));
        queue.Enqueue(Event(VendorEvent));
        var continueOnly = new NameParamSelector(VendorEvent, isFinal: false);

        var sw = Stopwatch.StartNew();
        var taken = queue.WaitEvents(continueOnly, Long, maxEvents: 2, maxTime: Long, finalEventCount: null);

        taken.Should().HaveCount(2);
        sw.Elapsed.Should().BeLessThan(Prompt);
        queue.Count.Should().Be(1);
    }

    [Fact]
    public void WaitEvents_MaxEventsReachedWithoutAFinalEvent_ReturnsInsteadOfThrowing()
    {
        var queue = new BgapiEventQueue();
        queue.Enqueue(Event(VendorEvent));
        var continueOnly = new NameParamSelector(VendorEvent, isFinal: false);

        var taken = queue.WaitEvents(continueOnly, Short, maxEvents: 1, maxTime: Short, finalEventCount: 1);

        taken.Should().ContainSingle();
        taken.FinalEventCount.Should().Be(0);
    }

    [Fact]
    public void WaitEvents_KeepAllEvents_TakesIgnoredEventsToo()
    {
        var queue = new BgapiEventQueue();
        var blockStatus = Event(BlockStatusEvent);
        var vendor = Event(VendorEvent);
        queue.Enqueue(blockStatus);
        queue.Enqueue(vendor);

        var taken = queue.WaitEvents(Select(VendorEvent), Short, maxTime: Short, keepAllEvents: true);

        taken.Should().Equal(blockStatus, vendor);
        taken.FinalEventCount.Should().Be(1);
        queue.Count.Should().Be(0);
    }

    [Fact]
    public void WaitEvents_SoftFinal_KeepsCollectingWithinTheProcessingWindow()
    {
        var queue = new BgapiEventQueue();
        var first = Event(VendorEvent);
        var second = Event(VendorEvent);
        queue.Enqueue(first);
        queue.Enqueue(second);

        var both = queue.WaitEvents(Select(VendorEvent), Long, maxTime: Long,
            finalEventCount: 2, softFinalEventCount: 1, softFinalEventProcTime: Short);

        both.Should().Equal(first, second);
        both.FinalEventCount.Should().Be(2);

        var third = Event(VendorEvent);
        queue.Enqueue(third);

        var sw = Stopwatch.StartNew();
        var one = queue.WaitEvents(Select(VendorEvent), Long, maxTime: Long,
            finalEventCount: 2, softFinalEventCount: 1, softFinalEventProcTime: Short);

        one.Should().ContainSingle().Which.Should().BeSameAs(third);
        sw.Elapsed.Should().BeLessThan(Prompt, "the soft window ends the wait, not the hard count");
    }

    // -1 ms is also Timeout.InfiniteTimeSpan. A wait overrun by exactly 1 ms — a late wake, a slow
    // selector — read as having no bound and waited for the next event, forever on a quiet bus.
    [Fact]
    public void Remaining_OneMillisecondPastTheBound_IsExpired()
    {
        var remaining = BgapiEventQueue.Remaining(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(101));

        BgapiEventQueue.Expired(remaining).Should().BeTrue();
    }

    // A waiter paused between its deadline check and its wait finds its ticket already completed,
    // by an event that came after the deadline. A completed ticket is not an arrival in time.
    [Fact]
    public void WaitEvents_PausedBeforeItsWait_LeavesAnEventThatArrivedAfterTheDeadline()
    {
        var queue = new BgapiEventQueue();
        var late = Event(VendorEvent);
        queue.BeforeArrival = () =>
        {
            queue.BeforeArrival = null;
            Thread.Sleep(Short * 2);
            queue.Enqueue(late);
        };

        var act = () => queue.WaitEvents(Select(VendorEvent), Short, maxTime: Short);

        act.Should().Throw<BgapiWaitEventException>();
        queue.GetEvent(TimeSpan.Zero).Should().BeSameAs(late);
    }

    // The list used to keep a set of the events it had counted, for RetryUntilAsync to take back
    // the count of one it dropped. A caller reusing one list across waits, clearing it between
    // them, kept every earlier event alive through that set.
    [Fact]
    public void WaitEvents_ReusedListCleared_KeepsNoClearedEventAlive()
    {
        var queue = new BgapiEventQueue();
        var reused = new BgapiEventList();
        var cleared = WaitInto(queue, reused);
        reused.Clear();
        reused.FinalEventCount = 0;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        cleared.IsAlive.Should().BeFalse();
        GC.KeepAlive(reused);
    }

    // Not inlined, so no local of the test's own frame keeps the event alive.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference WaitInto(BgapiEventQueue queue, BgapiEventList list)
    {
        var blockStatus = Event(BlockStatusEvent);
        queue.Enqueue(blockStatus);
        queue.WaitEvents(Select(BlockStatusEvent), Short, maxTime: Short, selectedEvents: list);
        return new WeakReference(blockStatus);
    }

    [Fact]
    public void WaitEvents_Deadline_IsTheSmallerOfTimeoutAndMaxTime()
    {
        var queue = new BgapiEventQueue();

        var sw = Stopwatch.StartNew();
        queue.WaitEvents(Select(VendorEvent), Long, maxTime: Short, finalEventCount: null);
        sw.Elapsed.Should().BeLessThan(Prompt, "maxTime bounds the wait");

        sw.Restart();
        queue.WaitEvents(Select(VendorEvent), Short, maxTime: Long, finalEventCount: null);
        sw.Elapsed.Should().BeLessThan(Prompt, "timeout bounds the wait");
    }

    [Fact]
    public void WaitEvents_SlowSelectorOverABacklog_StillEndsAtTheDeadline()
    {
        // The deadline is checked between candidates: a backlog or a slow selector must not carry
        // the call past its budget.
        var queue = new BgapiEventQueue();
        for (int i = 0; i < 20; i++) queue.Enqueue(Event(VendorEvent));
        var slowIgnore = new CallableSelector((_, _) => { Thread.Sleep(20); return EventCategory.Ignore; });

        var sw = Stopwatch.StartNew();
        var taken = queue.WaitEvents(slowIgnore, Short, maxTime: Short, finalEventCount: null);

        taken.Should().BeEmpty();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(300));
        queue.Count.Should().Be(20);
    }

    [Fact]
    public void WaitEvents_CancelledDuringTheScan_Throws()
    {
        var queue = new BgapiEventQueue();
        for (int i = 0; i < 100; i++) queue.Enqueue(Event(VendorEvent));
        var slowIgnore = new CallableSelector((_, _) => { Thread.Sleep(20); return EventCategory.Ignore; });
        using var cts = new CancellationTokenSource(Short);

        var sw = Stopwatch.StartNew();
        var act = () => queue.WaitEvents(slowIgnore, Long, maxTime: Long, finalEventCount: null, ct: cts.Token);

        act.Should().Throw<OperationCanceledException>();
        sw.Elapsed.Should().BeLessThan(Prompt, "cancellation is checked between candidates");
        queue.Count.Should().Be(100);
    }

    [Fact]
    public void WaitEvents_InfiniteTimeout_IsBoundedByMaxTime()
    {
        var queue = new BgapiEventQueue();

        var sw = Stopwatch.StartNew();
        var taken = queue.WaitEvents(Select(VendorEvent), Timeout.InfiniteTimeSpan, maxTime: Short, finalEventCount: null);

        taken.Should().BeEmpty();
        sw.Elapsed.Should().BeLessThan(Prompt);
    }

    [Fact]
    public void GetEvent_InfiniteTimeout_BlocksUntilAnEventArrives()
    {
        var queue = new BgapiEventQueue();
        var vendor = Event(VendorEvent);
        var producer = new Thread(() =>
        {
            Thread.Sleep(Short);
            queue.Enqueue(vendor);
        });
        producer.Start();

        var taken = queue.GetEvent(Timeout.InfiniteTimeSpan);

        taken.Should().BeSameAs(vendor);
        producer.Join(Long).Should().BeTrue();
    }

    [Fact]
    public void Enqueue_BeyondMaxQueuedEvents_DropsTheOldestAndCounts()
    {
        var queue = QueueWithLimit(2);
        var first = Event(VendorEvent);
        var second = Event(VendorEvent);
        var third = Event(VendorEvent);

        queue.Enqueue(first);
        queue.Enqueue(second);
        queue.Enqueue(third);

        queue.Count.Should().Be(2);
        queue.DroppedEventCount.Should().Be(1);
        queue.GetEvent(TimeSpan.Zero).Should().BeSameAs(second);
        queue.GetEvent(TimeSpan.Zero).Should().BeSameAs(third);
    }

    [Fact]
    public void GetEvent_ReturnsOldestFirst_AndNullOnTimeout()
    {
        var queue = new BgapiEventQueue();
        var first = Event(VendorEvent);
        var second = Event(BlockStatusEvent);
        queue.Enqueue(first);
        queue.Enqueue(second);

        queue.GetEvent(Short).Should().BeSameAs(first);
        queue.GetEvent(Short).Should().BeSameAs(second);

        var sw = Stopwatch.StartNew();
        queue.GetEvent(Short).Should().BeNull();
        sw.Elapsed.Should().BeLessThan(Prompt);
    }

    [Fact]
    public void Clear_EmptiesTheQueue()
    {
        var queue = new BgapiEventQueue();
        queue.Enqueue(Event(VendorEvent));
        queue.Enqueue(Event(BlockStatusEvent));

        queue.Clear();

        queue.Count.Should().Be(0);
        queue.GetEvent(TimeSpan.Zero).Should().BeNull();
    }

    // WaitEvents took paramSubs and never read them, so a "$name" constraint kept its literal
    // "$address" string and the wait could only time out — the path every RetryUntilAsync wait
    // takes with the command response.

    [Fact]
    public void WaitEvents_ParamSubs_ResolveDollarPlaceholders()
    {
        var queue = new BgapiEventQueue();
        var fromNode = new BgapiMessage(Event(VendorEvent).Header, ReadOnlyMemory<byte>.Empty)
        {
            EventName = VendorEvent,
            Parameters = new Dictionary<string, object> { { "source_address", (ushort)256 } },
        };
        queue.Enqueue(fromNode);
        var selector = new NameParamSelector(VendorEvent,
            new Dictionary<string, object> { { "source_address", "$address" } });

        var taken = queue.WaitEvents(selector, Short, maxTime: Short,
            paramSubs: new Dictionary<string, object> { { "address", (ushort)256 } });

        taken.Should().ContainSingle().Which.Should().BeSameAs(fromNode);
    }
}
