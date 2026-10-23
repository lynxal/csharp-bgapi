using CsharpBgapi.Events;
using CsharpBgapi.Protocol;
using CsharpBgapi.Validation;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CsharpBgapi.Tests;

public class BgapiEventQueueValidationTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static readonly XapiDefinitions Definitions = Wire.LoadDefinitions();
    private static BgapiMessage Event(int id) => new BgapiProtocol(Definitions).DecodeMessage(Wire.Event(Definitions, id));

    [Fact]
    public async Task WaitEvents_SignaledWaiterPaused_OverflowCanEvictItsEvent()
    {
        var queue = new BgapiEventQueue(Options.Create(new CsharpBgapiOptions { MaxQueuedEvents = 1 }), NullLogger.Instance);
        using var scanning = new ManualResetEventSlim();
        using var woken = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        queue.BeforeArrival = scanning.Set;
        queue.AfterArrival = () => { woken.Set(); if (!resume.Wait(Guard)) throw new TimeoutException("waiter guard"); };
        queue.Enqueue(Event(-1));
        var selector = new CallableSelector((message, _) =>
        {
            if (Wire.Sequence(message) < 0) return EventCategory.Ignore;
            return EventCategory.SelectFinal;
        });
        var waiter = Task.Run(() => queue.WaitEvent(selector, Guard));
        try
        {
            scanning.Wait(Guard).Should().BeTrue();
            queue.Enqueue(Event(1));
            woken.Wait(Guard).Should().BeTrue();
            long before = queue.DroppedEventCount;
            queue.Enqueue(Event(2));
            queue.DroppedEventCount.Should().Be(before + 1);
        }
        finally { resume.Set(); }
        (await waiter.WaitAsync(Guard)).Select(Wire.Sequence).Should().Equal(2);
        queue.Count.Should().Be(0);
    }

    [Fact]
    public async Task WaitEvents_ClearWhileSelectorPaused_DoesNotClaimRemovedEventOrMissNewArrival()
    {
        var queue = new BgapiEventQueue();
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        queue.Enqueue(Event(1));
        var selector = new CallableSelector((message, _) =>
        {
            if (Wire.Sequence(message) == 1)
            {
                entered.Set();
                if (!resume.Wait(Guard)) throw new TimeoutException("selector guard");
            }
            return EventCategory.SelectFinal;
        });
        var waiter = Task.Run(() => queue.WaitEvent(selector, Guard));
        try
        {
            entered.Wait(Guard).Should().BeTrue();
            queue.Clear();
            queue.Enqueue(Event(2));
        }
        finally { resume.Set(); }
        (await waiter.WaitAsync(Guard)).Select(Wire.Sequence).Should().Equal(2);
        queue.DroppedEventCount.Should().Be(0);
    }

    [Fact]
    public void WaitEvents_ThrowingSelector_LeavesCandidateAndUnrelatedEventsInOrder()
    {
        var queue = new BgapiEventQueue();
        queue.Enqueue(Event(1));
        queue.Enqueue(Event(2));
        var selector = new CallableSelector((_, _) => throw new InvalidOperationException("injected selector failure"));
        queue.Invoking(q => q.WaitEvent(selector, Guard)).Should().Throw<InvalidOperationException>()
            .WithMessage("injected selector failure");
        Wire.Sequence(queue.GetEvent(TimeSpan.Zero)!).Should().Be(1);
        Wire.Sequence(queue.GetEvent(TimeSpan.Zero)!).Should().Be(2);
    }

    [Fact]
    public async Task WaitEvents_CompetingSelectorsPausedOnSameCandidate_ClaimItAtMostOnce()
    {
        var queue = new BgapiEventQueue();
        queue.Enqueue(Event(1));
        queue.Enqueue(Event(2));
        using var both = new Barrier(2);
        var selector = new CallableSelector((message, _) =>
        {
            if (Wire.Sequence(message) == 1 && !both.SignalAndWait(Guard)) throw new TimeoutException("claim guard");
            return EventCategory.SelectFinal;
        });
        var results = await Task.WhenAll(Task.Run(() => queue.WaitEvent(selector, Guard)),
            Task.Run(() => queue.WaitEvent(selector, Guard))).WaitAsync(Guard);
        results.SelectMany(r => r).Select(Wire.Sequence).Should().BeEquivalentTo([1, 2]);
    }
}
