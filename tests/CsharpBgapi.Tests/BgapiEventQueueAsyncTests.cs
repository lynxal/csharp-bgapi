using System.Collections.Concurrent;
using CsharpBgapi.Events;
using CsharpBgapi.Protocol;
using FluentAssertions;
using Xunit;

namespace CsharpBgapi.Tests;

public class BgapiEventQueueAsyncTests
{
    // WaitEventsAsync is WaitEvents with the wait for the next event awaited instead of blocked.
    // It must keep WaitEvents' contract — take only what its selector selects, leave every other
    // event queued in arrival order, throw on cancellation or an unmet final count — and hand its
    // task back without holding the caller's thread while nothing has arrived.

    private const string VendorEvent = "btmesh_evt_vendor_model_receive";
    private const string BlockStatusEvent = "btmesh_evt_mbt_client_query_block_status_complete";

    // A wait expected to time out gets 10 ms; one expected to succeed gets a guard it never reaches.
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);

    // Event flag 0xA0 with device id 5 (btmesh) in bits 3-6, class 0, index 0, no payload. The
    // queue and NameParamSelector match on EventName alone, so the indices are never read.
    private static BgapiMessage Event(string name)
    {
        var header = new BgapiHeader((byte)(BgapiHeader.EventFlag | (5 << 3)), payloadLength: 0, classIndex: 0, commandIndex: 0);
        return new BgapiMessage(header, ReadOnlyMemory<byte>.Empty) { EventName = name };
    }

    private static NameParamSelector Select(string name) => new(name);

    [Fact]
    public async Task WaitEventsAsync_NothingQueued_ReturnsAnIncompleteTaskThatCompletesOnArrival()
    {
        var queue = new BgapiEventQueue();

        var wait = queue.WaitEventsAsync(Select(BlockStatusEvent), Guard, maxTime: Guard);

        wait.IsCompleted.Should().BeFalse("the call must hand back a task, not block until an event arrives");
        var blockStatus = Event(BlockStatusEvent);
        queue.Enqueue(blockStatus);
        var taken = await wait.WaitAsync(Guard);
        taken.Should().ContainSingle().Which.Should().BeSameAs(blockStatus);
        queue.Count.Should().Be(0);
    }

    [Fact]
    public async Task WaitEventsAsync_EventQueuedBeforeTheCall_IsFound()
    {
        var queue = new BgapiEventQueue();
        var blockStatus = Event(BlockStatusEvent);
        queue.Enqueue(blockStatus);

        var taken = await queue.WaitEventsAsync(Select(BlockStatusEvent), Guard, maxTime: Guard).WaitAsync(Guard);

        taken.Should().ContainSingle().Which.Should().BeSameAs(blockStatus);
        taken.FinalEventCount.Should().Be(1);
        queue.Count.Should().Be(0);
    }

    [Fact]
    public async Task WaitEventsAsync_NonMatchingEvents_StayQueuedInArrivalOrder()
    {
        var queue = new BgapiEventQueue();
        var vendor1 = Event(VendorEvent);
        var blockStatus = Event(BlockStatusEvent);
        var vendor2 = Event(VendorEvent);
        queue.Enqueue(vendor1);
        queue.Enqueue(blockStatus);
        queue.Enqueue(vendor2);

        var taken = await queue.WaitEventsAsync(Select(BlockStatusEvent), Guard, maxTime: Guard).WaitAsync(Guard);

        taken.Should().ContainSingle().Which.Should().BeSameAs(blockStatus);
        queue.Count.Should().Be(2);
        queue.GetEvent(TimeSpan.Zero).Should().BeSameAs(vendor1);
        queue.GetEvent(TimeSpan.Zero).Should().BeSameAs(vendor2);
    }

    [Fact]
    public async Task WaitEventsAsync_CancelledMidWait_ThrowsAndLeavesExaminedEventsQueued()
    {
        var queue = new BgapiEventQueue();
        var vendor = Event(VendorEvent);
        queue.Enqueue(vendor);
        using var cts = new CancellationTokenSource();

        var wait = queue.WaitEventsAsync(Select(BlockStatusEvent), Guard, maxTime: Guard, ct: cts.Token);
        wait.IsCompleted.Should().BeFalse();
        cts.Cancel();

        var act = () => wait.WaitAsync(Guard);
        await act.Should().ThrowAsync<OperationCanceledException>();
        queue.Count.Should().Be(1, "an event the cancelled wait examined and ignored must still be there");
        queue.GetEvent(TimeSpan.Zero).Should().BeSameAs(vendor);
    }

    [Fact]
    public async Task WaitEventsAsync_TokenAlreadyCancelled_ThrowsOperationCanceled()
    {
        var queue = new BgapiEventQueue();
        queue.Enqueue(Event(VendorEvent));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => queue.WaitEventsAsync(Select(VendorEvent), Guard, maxTime: Guard, ct: cts.Token).WaitAsync(Guard);

        await act.Should().ThrowAsync<OperationCanceledException>();
        queue.Count.Should().Be(1);
    }

    [Fact]
    public async Task WaitEventsAsync_FinalCountNotReached_ThrowsWithCollectedEvents()
    {
        var queue = new BgapiEventQueue();
        var vendor = Event(VendorEvent);
        queue.Enqueue(vendor);

        var act = () => queue.WaitEventsAsync(Select(VendorEvent), Short, maxTime: Short, finalEventCount: 2).WaitAsync(Guard);

        var thrown = await act.Should().ThrowAsync<BgapiWaitEventException>();
        thrown.Which.CollectedEvents.Should().ContainSingle().Which.Should().BeSameAs(vendor);
    }

    [Fact]
    public async Task WaitEventsAsync_InfiniteBound_IsLimitedByTheOther()
    {
        var queue = new BgapiEventQueue();

        var infiniteTimeout = () => queue.WaitEventsAsync(Select(VendorEvent), Timeout.InfiniteTimeSpan, maxTime: Short).WaitAsync(Guard);
        var infiniteMaxTime = () => queue.WaitEventsAsync(Select(VendorEvent), Short, maxTime: Timeout.InfiniteTimeSpan).WaitAsync(Guard);

        await infiniteTimeout.Should().ThrowAsync<BgapiWaitEventException>("maxTime still ends a wait whose timeout is infinite");
        await infiniteMaxTime.Should().ThrowAsync<BgapiWaitEventException>("timeout still ends a wait whose maxTime is infinite");
    }

    [Fact]
    public async Task WaitEventsAsync_SoftFinalReached_AwaitsTheProcessingWindowForTheHardCount()
    {
        var queue = new BgapiEventQueue();
        var first = Event(VendorEvent);
        queue.Enqueue(first);

        var wait = queue.WaitEventsAsync(Select(VendorEvent), Guard, maxTime: Guard,
            finalEventCount: 2, softFinalEventCount: 1, softFinalEventProcTime: Guard);

        wait.IsCompleted.Should().BeFalse("the soft window must hand back a task, not block until the hard count");
        var second = Event(VendorEvent);
        queue.Enqueue(second);
        var both = await wait.WaitAsync(Guard);
        both.Should().Equal(first, second);
        both.FinalEventCount.Should().Be(2);
    }

    [Fact]
    public async Task WaitEventsAsync_SoftWindowEnds_ReturnsWhatItCollected()
    {
        var queue = new BgapiEventQueue();
        var first = Event(VendorEvent);
        queue.Enqueue(first);

        var one = await queue.WaitEventsAsync(Select(VendorEvent), Guard, maxTime: Guard,
            finalEventCount: 2, softFinalEventCount: 1, softFinalEventProcTime: Short).WaitAsync(Guard);

        one.Should().ContainSingle().Which.Should().BeSameAs(first);
        one.FinalEventCount.Should().Be(1);
    }

    [Fact]
    public async Task WaitEventsAsync_ResumedAfterTheDeadline_TakesTheEventThatWokeIt()
    {
        // An awaiting waiter resumes only once a pool thread is free, which can be after its
        // deadline. The event that woke it arrived in time; it used to stay queued while the call
        // reported a timeout, and RetryUntilAsync re-sent a command whose reply was already there.
        var queue = new BgapiEventQueue();
        var window = TimeSpan.FromMilliseconds(250);
        using var waiting = new ManualResetEventSlim();
        queue.BeforeArrival = waiting.Set;
        queue.AfterArrival = () => Thread.Sleep(window * 2);
        var wait = queue.WaitEventsAsync(Select(BlockStatusEvent), window, maxTime: window);
        waiting.Wait(Guard).Should().BeTrue();

        var blockStatus = Event(BlockStatusEvent);
        queue.Enqueue(blockStatus);

        (await wait.WaitAsync(Guard)).Should().ContainSingle().Which.Should().BeSameAs(blockStatus);
        queue.Count.Should().Be(0);
    }

    [Fact]
    public async Task WaitEventsAsync_ResumedAfterTheDeadline_LeavesAnEventThatArrivedLate()
    {
        // Past the deadline only the event that woke the waiter is examined. Another consumer took
        // it first, and the one behind it arrived after the deadline, so the wait times out.
        var queue = new BgapiEventQueue();
        var window = TimeSpan.FromMilliseconds(250);
        using var waiting = new ManualResetEventSlim();
        using var woken = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        queue.BeforeArrival = waiting.Set;
        queue.AfterArrival = () => { woken.Set(); if (!resume.Wait(Guard)) throw new TimeoutException("waiter guard"); };
        var wait = queue.WaitEventsAsync(Select(BlockStatusEvent), window, maxTime: window);
        waiting.Wait(Guard).Should().BeTrue();

        queue.Enqueue(Event(BlockStatusEvent));
        woken.Wait(Guard).Should().BeTrue();
        queue.GetEvent(TimeSpan.Zero).Should().NotBeNull("another consumer takes the event that woke the waiter");
        Thread.Sleep(window * 2);
        var late = Event(BlockStatusEvent);
        queue.Enqueue(late);
        resume.Set();

        var act = () => wait.WaitAsync(Guard);
        await act.Should().ThrowAsync<BgapiWaitEventException>();
        queue.GetEvent(TimeSpan.Zero).Should().BeSameAs(late);
    }

    [Fact]
    public async Task WaitEventsAsync_EventArrivingAfterTheWaitTimedOut_IsLeftQueued()
    {
        // The window ran out first and the event arrived before the waiter resumed. It completed
        // the ticket the waiter held, but it came late, so the wait times out without it.
        var queue = new BgapiEventQueue();
        var context = new QueuedContext();
        Task<BgapiEventList> wait;
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try { wait = queue.WaitEventsAsync(Select(BlockStatusEvent), Short, maxTime: Short); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }

        SpinWait.SpinUntil(() => context.HasWork, Guard).Should().BeTrue("the window runs out before the event arrives");
        var late = Event(BlockStatusEvent);
        queue.Enqueue(late);
        context.RunAll();

        var act = () => wait.WaitAsync(Guard);
        await act.Should().ThrowAsync<BgapiWaitEventException>();
        queue.GetEvent(TimeSpan.Zero).Should().BeSameAs(late);
    }

    // Holds every continuation posted to it until RunAll, so a test decides when a waiter resumes.
    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _posted = new();

        public bool HasWork => !_posted.IsEmpty;

        public override void Post(SendOrPostCallback d, object? state) => _posted.Enqueue((d, state));

        public void RunAll()
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                while (_posted.TryDequeue(out var work)) work.Callback(work.State);
            }
            finally { SetSynchronizationContext(previous); }
        }
    }

    [Fact]
    public async Task WaitEventsAsync_BesideASyncWaiter_EachTakesOnlyItsOwnEvent()
    {
        var queue = new BgapiEventQueue();
        var asyncWait = queue.WaitEventsAsync(Select(VendorEvent), Guard, maxTime: Guard);
        // LongRunning blocks a dedicated thread, as a sync caller does, and carries a throw back
        // to the test instead of crashing the test host.
        var syncWait = Task.Factory.StartNew(() => queue.WaitEvents(Select(BlockStatusEvent), Guard, maxTime: Guard),
            TaskCreationOptions.LongRunning);

        var blockStatus = Event(BlockStatusEvent);
        var vendor = Event(VendorEvent);
        queue.Enqueue(blockStatus);
        queue.Enqueue(vendor);

        (await asyncWait.WaitAsync(Guard)).Should().ContainSingle().Which.Should().BeSameAs(vendor);
        (await syncWait.WaitAsync(Guard)).Should().ContainSingle().Which.Should().BeSameAs(blockStatus);
        queue.Count.Should().Be(0);
    }

    [Fact]
    public async Task WaitEventsAsync_EventArrivingDuringTheWait_SelectorRunsOffTheEnqueueingThread()
    {
        // Enqueue runs on the reader thread. A waiter resumed inline there would run its selector
        // — caller code — inside Enqueue, and a slow selector would stall serial reads.
        // Threads are compared as objects: a managed thread id is reused once its thread exits.
        var queue = new BgapiEventQueue();
        Thread? selectorThread = null;
        var selector = new CallableSelector((message, _) =>
        {
            selectorThread = Thread.CurrentThread;
            return message.EventName == VendorEvent ? EventCategory.SelectFinal : EventCategory.Ignore;
        });
        var wait = queue.WaitEventsAsync(selector, Guard, maxTime: Guard);
        wait.IsCompleted.Should().BeFalse();

        var reader = new Thread(() => queue.Enqueue(Event(VendorEvent)));
        reader.Start();
        reader.Join(Guard).Should().BeTrue();

        (await wait.WaitAsync(Guard)).Should().ContainSingle();
        selectorThread.Should().NotBeSameAs(reader, "an async waiter must resume off the thread that enqueued the event");
    }
}
