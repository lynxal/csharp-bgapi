using CsharpBgapi.Events;
using CsharpBgapi.Protocol;
using FluentAssertions;
using Xunit;

namespace CsharpBgapi.Tests;

public class BgapiEventQueueRetryUntilTests
{
    private const string VendorReceive = "btmesh_evt_vendor_model_receive";

    // Zero intervals so a retried command runs at once; a wait that has to time out gets 10 ms.
    private static readonly RetryParams Fast = new()
    {
        RetryMax = 2,
        RetryInterval = TimeSpan.FromMilliseconds(10),
        RetryCmdMax = 2,
        RetryCmdInterval = TimeSpan.Zero,
    };

    private static BgapiMessage Event(string name, ushort? sourceAddress = null)
    {
        var header = new BgapiHeader((byte)(BgapiHeader.EventFlag | (5 << 3)), payloadLength: 0, classIndex: 0, commandIndex: 0);
        return new BgapiMessage(header, ReadOnlyMemory<byte>.Empty)
        {
            EventName = name,
            Parameters = sourceAddress is { } address
                ? new Dictionary<string, object> { { "source_address", address } }
                : null,
        };
    }

    private static BgapiCommandResponse Response(SlStatus status, Dictionary<string, object>? parameters = null) =>
        new() { Status = status, Parameters = parameters };

    /// <summary>A command that answers with each status in turn and counts its calls.</summary>
    private sealed class ScriptedCommand(params BgapiCommandResponse[] responses)
    {
        public int Calls { get; private set; }
        public Action<int>? BeforeAnswering { get; init; }

        public Task<BgapiCommandResponse> Invoke()
        {
            BeforeAnswering?.Invoke(Calls);
            var response = responses[Math.Min(Calls, responses.Length - 1)];
            Calls++;
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task RetryUntilAsync_TransientStatus_RetriesTheCommandThenReturnsTheEvents()
    {
        var queue = new BgapiEventQueue();
        var expected = Event(VendorReceive);
        queue.Enqueue(expected);
        var command = new ScriptedCommand(Response(SlStatus.Busy), Response(SlStatus.NoMoreResource), Response(SlStatus.OK));

        var events = await queue.RetryUntilAsync(command.Invoke, new NameParamSelector(VendorReceive), Fast);

        events.Should().ContainSingle().Which.Should().BeSameAs(expected);
        command.Calls.Should().Be(3);
    }

    [Fact]
    public async Task RetryUntilAsync_NonTransientStatus_ThrowsAtOnce()
    {
        var queue = new BgapiEventQueue();
        var command = new ScriptedCommand(Response(SlStatus.InvalidParameter));

        var act = () => queue.RetryUntilAsync(command.Invoke, new NameParamSelector(VendorReceive), Fast);

        (await act.Should().ThrowAsync<BgapiCommandException>()).Which.Status.Should().Be(SlStatus.InvalidParameter);
        command.Calls.Should().Be(1, "only Busy and NoMoreResource are retried by default");
    }

    [Fact]
    public async Task RetryUntilAsync_TransientUntilAttemptsRunOut_ThrowsWithTheLastStatus()
    {
        var queue = new BgapiEventQueue();
        var command = new ScriptedCommand(Response(SlStatus.Busy));

        var act = () => queue.RetryUntilAsync(command.Invoke, new NameParamSelector(VendorReceive), Fast);

        (await act.Should().ThrowAsync<BgapiCommandException>()).Which.Status.Should().Be(SlStatus.Busy);
        command.Calls.Should().Be(Fast.RetryCmdMax + 1);
    }

    [Fact]
    public async Task RetryUntilAsync_NullSelector_ReturnsAnEmptyListOnceTheCommandSucceeds()
    {
        var queue = new BgapiEventQueue();
        queue.Enqueue(Event(VendorReceive));
        var command = new ScriptedCommand(Response(SlStatus.OK));

        var events = await queue.RetryUntilAsync(command.Invoke, eventSelector: null, Fast);

        events.Should().BeEmpty();
        command.Calls.Should().Be(1);
        queue.Count.Should().Be(1, "with no selector nothing is waited for or taken");
    }

    [Fact]
    public async Task RetryUntilAsync_EventsNeverArrive_RetriesThenThrowsWaitEventException()
    {
        var queue = new BgapiEventQueue();
        var command = new ScriptedCommand(Response(SlStatus.OK));

        var act = () => queue.RetryUntilAsync(command.Invoke, new NameParamSelector(VendorReceive), Fast);

        await act.Should().ThrowAsync<BgapiWaitEventException>();
        command.Calls.Should().Be(Fast.RetryMax + 1, "each outer attempt re-issues the command before waiting again");
    }

    // RetryUntilAsync passes the command response to WaitEvents as paramSubs so a "$name"
    // constraint can match the value the command just returned. WaitEvents never read them, so the
    // selector kept the literal "$address" and a wait for the reply from the addressed node could
    // only time out. On a later attempt whose command failed transiently, the wait also ran with
    // no substitutions at all: paramSubs was declared per attempt, not kept from the last success.

    [Fact]
    public async Task RetryUntilAsync_DollarPlaceholder_ResolvesFromTheCommandResponse()
    {
        var queue = new BgapiEventQueue();
        var otherNode = Event(VendorReceive, sourceAddress: 512);
        var addressedNode = Event(VendorReceive, sourceAddress: 256);
        queue.Enqueue(otherNode);
        queue.Enqueue(addressedNode);
        var command = new ScriptedCommand(Response(SlStatus.OK, new Dictionary<string, object> { { "address", (ushort)256 } }));
        var selector = new NameParamSelector(VendorReceive,
            new Dictionary<string, object> { { "source_address", "$address" } });

        var events = await queue.RetryUntilAsync(command.Invoke, selector, Fast);

        events.Should().ContainSingle().Which.Should().BeSameAs(addressedNode);
        queue.Count.Should().Be(1, "the other node's event is not the one asked for");
    }

    [Fact]
    public async Task RetryUntilAsync_TransientAfterASuccess_WaitsWithThatSuccessesResponse()
    {
        var queue = new BgapiEventQueue();
        var addressedNode = Event(VendorReceive, sourceAddress: 256);
        // Attempt 1 succeeds and its wait times out empty; attempt 2's command is Busy, and the
        // event the first command asked for lands just before that answer.
        var command = new ScriptedCommand(
            Response(SlStatus.OK, new Dictionary<string, object> { { "address", (ushort)256 } }),
            Response(SlStatus.Busy))
        {
            BeforeAnswering = call => { if (call == 1) queue.Enqueue(addressedNode); },
        };
        var selector = new NameParamSelector(VendorReceive,
            new Dictionary<string, object> { { "source_address", "$address" } });

        var events = await queue.RetryUntilAsync(command.Invoke, selector,
            Fast with { RetryCmdInterval = TimeSpan.FromMilliseconds(10) });

        events.Should().ContainSingle().Which.Should().BeSameAs(addressedNode);
        command.Calls.Should().Be(2);
    }

    // After a wait that fell short, the next attempt started from a copy of the collected events
    // whose final count was reset to 0. A stateful selector keeps those events, so the ones already
    // counted were never counted again: with #unique over the nodes, the target was out of reach and
    // every retry failed.

    [Fact]
    public async Task RetryUntilAsync_WaitFallsShort_KeepsTheFinalCountForTheNextAttempt()
    {
        var queue = new BgapiEventQueue();
        var firstNode = Event(VendorReceive, sourceAddress: 256);
        var secondNode = Event(VendorReceive, sourceAddress: 512);
        queue.Enqueue(firstNode);
        var command = new ScriptedCommand(Response(SlStatus.OK))
        {
            BeforeAnswering = call => { if (call == 1) queue.Enqueue(secondNode); },
        };
        var selector = new NameParamSelector(VendorReceive,
            new Dictionary<string, object> { { "#unique", "source_address" } });

        var events = await queue.RetryUntilAsync(command.Invoke, selector, Fast, finalEventCount: 2);

        events.Should().Equal(firstNode, secondNode);
        command.Calls.Should().Be(2);
    }

    // The retry event selector was matched as written, never resolved against the command
    // response, so a "$address" constraint on the failure event never matched: the node reported
    // failure and RetryUntilAsync returned it as the result instead of retrying.

    [Fact]
    public async Task RetryUntilAsync_RetryEventSelector_ResolvesDollarPlaceholders()
    {
        const string failed = "btmesh_evt_vendor_model_send_complete";
        var queue = new BgapiEventQueue();
        var failure = Event(failed, sourceAddress: 256);
        var success = Event(VendorReceive, sourceAddress: 256);
        queue.Enqueue(failure);
        var command = new ScriptedCommand(Response(SlStatus.OK, new Dictionary<string, object> { { "address", (ushort)256 } }))
        {
            BeforeAnswering = call => { if (call == 1) queue.Enqueue(success); },
        };
        var fromAddress = new Dictionary<string, object> { { "source_address", "$address" } };
        var selector = new CompositeSelector(
            new NameParamSelector(VendorReceive, fromAddress),
            new NameParamSelector(failed, fromAddress));

        var events = await queue.RetryUntilAsync(command.Invoke, selector, Fast,
            retryEventSelector: new NameParamSelector(failed, fromAddress));

        events.Should().ContainSingle().Which.Should().BeSameAs(success);
        command.Calls.Should().Be(2);
    }
}
