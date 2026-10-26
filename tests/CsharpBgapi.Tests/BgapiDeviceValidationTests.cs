using CsharpBgapi.Protocol;
using CsharpBgapi.Events;
using CsharpBgapi.Serial;
using CsharpBgapi.Validation;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CsharpBgapi.Tests;

public class BgapiDeviceValidationTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Session : IDisposable
    {
        public SimulatedConnection Port { get; } = new();
        public BgapiDevice Device { get; }
        public Session(ILogger? logger = null, double stopSeconds = 1)
        {
            var options = Options.Create(new CsharpBgapiOptions { StopReaderTimeoutSeconds = stopSeconds });
            Device = new BgapiDevice(options, new BgapiConnector(options, NullLogger.Instance, (_, _) => Port), logger);
            Device.LoadDefaultXapis();
            Device.Open("simulated");
        }
        public Task<BgapiCommandResponse> Send(CancellationToken ct = default) =>
            Device.SendCommandAsync("bt", "system", "halt", ct: ct);
        public byte[] Reply(ushort status = 0) => Wire.Response(Device.Definitions, status: status);
        public byte[] ReplyTo(byte[] written) => Wire.ResponseTo(Device.Definitions, written);
        public string[] Written => [.. Port.Writes.Select(written =>
        {
            var header = BgapiHeader.Parse(written);
            return Device.Definitions.FindCommand(header.DeviceId, header.ClassIndex, header.CommandIndex)!.Name;
        })];
        public void Dispose() { Device.Dispose(); Port.Dispose(); }
    }

    [Fact]
    public async Task SendCommandAsync_CompletedResponseThenTimeout_AlreadyCompletedResponseWins()
    {
        using var session = new Session();
        (await session.Port.ReadStarted.WaitAsync(Guard)).Should().BeTrue();
        session.Port.OnWrite = _ => session.Port.Inject(session.Reply());
        session.Device.ScheduleResponseTimeout = (source, _) =>
        {
            // Starting the next read proves HandleResponseMessage completed the pending TCS.
            session.Port.ReadStarted.Wait(Guard).Should().BeTrue();
            source.Cancel();
        };
        (await session.Send().WaitAsync(Guard)).Status.Should().Be(SlStatus.OK);
    }

    [Fact]
    public async Task Reader_ReadFault_ContinuesToNextEvent()
    {
        var failure = Signal<bool>();
        using var session = new Session(new HookLogger(text =>
        {
            if (text.Contains("ReaderLoop exception")) failure.TrySetResult(true);
        }));
        var delivered = Signal<int>();
        session.Device.Subscribe("bt_evt_user_message_to_host", message => delivered.TrySetResult(Wire.Sequence(message)));
        session.Port.Fault(new IOException("injected disconnect"));
        await failure.Task.WaitAsync(Guard);
        session.Port.Inject(Wire.Event(session.Device.Definitions, 37));
        (await delivered.Task.WaitAsync(Guard)).Should().Be(37);
    }

    [Fact]
    public async Task RetryUntilAsync_ReaderEventsDuringTransientCommand_PreservesUnrelatedEvent()
    {
        using var session = new Session();
        int attempts = 0;
        session.Port.OnWrite = _ =>
        {
            int attempt = ++attempts;
            session.Port.Inject([.. Wire.Event(session.Device.Definitions, attempt),
                .. session.Reply((ushort)(attempt == 1 ? SlStatus.Busy : SlStatus.OK))]);
        };
        var events = await session.Device.RetryUntilAsync(() => session.Send(),
            new CallableSelector((message, _) => Wire.Sequence(message) == 2 ? EventCategory.SelectFinal : EventCategory.Ignore),
            new RetryParams { RetryMax = 1, RetryCmdMax = 2, RetryCmdInterval = TimeSpan.Zero, RetryInterval = Guard }).WaitAsync(Guard);
        events.Select(Wire.Sequence).Should().Equal(2);
        Wire.Sequence(session.Device.EventQueue.GetEvent(TimeSpan.Zero)!).Should().Be(1);
        attempts.Should().Be(2);
    }

    [Fact]
    public async Task OpenClose_RepeatedSessions_CommandsRemainUsableAndPortsClose()
    {
        var ports = new List<SimulatedConnection>();
        var options = Options.Create(new CsharpBgapiOptions { ReaderLoopReadTimeoutMs = 1 });
        using var device = new BgapiDevice(options, new BgapiConnector(options, NullLogger.Instance, (_, _) =>
        {
            var port = new SimulatedConnection();
            ports.Add(port);
            return port;
        }));
        device.LoadDefaultXapis();
        try
        {
            for (int i = 0; i < 30; i++)
            {
                device.Open("simulated");
                var port = ports[^1];
                port.OnWrite = _ => port.Inject(Wire.Response(device.Definitions));
                (await device.SendCommandAsync("bt", "system", "halt").WaitAsync(Guard)).Status.Should().Be(SlStatus.OK);
                device.Close();
                port.IsOpen.Should().BeFalse();
                port.OverlappingReads.Should().Be(0);
            }
        }
        finally { foreach (IDisposable port in ports) port.Dispose(); }
    }

    [Fact]
    public async Task SendCommandAsync_ConcurrentCallersAndImmediateReplies_SerializeAndOwnTheirResponses()
    {
        using var session = new Session();
        int sent = 0;
        session.Port.OnWrite = _ => session.Port.Inject(session.Reply((ushort)(Interlocked.Increment(ref sent) % 2 == 0 ? SlStatus.Busy : SlStatus.OK)));
        var tasks = Enumerable.Range(0, 100).Select(_ => session.Send()).ToArray();
        var responses = await Task.WhenAll(tasks).WaitAsync(Guard);
        responses.Select(r => r.Status).Should().Equal(Enumerable.Range(1, 100).Select(i => i % 2 == 0 ? SlStatus.Busy : SlStatus.OK));
        session.Port.Writes.Should().HaveCount(100);
        session.Port.OverlappingWrites.Should().Be(0);
        session.Port.OverlappingReads.Should().Be(0);
        session.Port.CancellableReads.Should().Be(0);
    }

    [Fact]
    public async Task SendCommandAsync_CancellationBeforeLockAndAfterSend_ReleasesPendingSlot()
    {
        using var session = new Session();
        using var activeCancellation = new CancellationTokenSource();
        using var waitingCancellation = new CancellationTokenSource();
        var active = session.Send(activeCancellation.Token);
        var waiting = session.Send(waitingCancellation.Token);
        waitingCancellation.Cancel();
        await ((Func<Task>)(() => waiting)).Should().ThrowAsync<OperationCanceledException>();
        session.Port.Writes.Should().ContainSingle();
        activeCancellation.Cancel();
        await ((Func<Task>)(() => active)).Should().ThrowAsync<OperationCanceledException>();
        session.Port.OnWrite = written => session.Port.Inject(session.ReplyTo(written));
        (await session.Send().WaitAsync(Guard)).Status.Should().Be(SlStatus.OK);
        // The cancelled halt may still be answered, so the repeat is fenced.
        session.Written.Should().Equal("halt", "hello", "halt");
    }

    // Cancellation was read only by the waits, after each write. Cancelled once the fence had been
    // answered but before its wait resumed, the repeat was still written, then reported cancelled.
    [Fact]
    public async Task SendCommandAsync_CancelledAfterTheFenceIsAnswered_DoesNotWriteTheRepeat()
    {
        using var session = new Session();
        session.Device.ScheduleResponseTimeout = (source, _) => source.Cancel();
        (await session.Send().WaitAsync(Guard)).Status.Should().Be(SlStatus.Timeout);

        var routed = Signal<bool>();
        // Dispatched on the reader once it has routed the answer injected before the event.
        session.Device.Subscribe("bt_evt_user_message_to_host", _ => routed.TrySetResult(true));
        session.Port.OnWrite = written =>
            session.Port.Inject([.. session.ReplyTo(written), .. Wire.Event(session.Device.Definitions, 1)]);
        using var cancellation = new CancellationTokenSource();
        session.Device.ScheduleResponseTimeout = (_, _) =>
        {
            routed.Task.Wait(Guard).Should().BeTrue();
            cancellation.Cancel();
        };
        await ((Func<Task>)(() => session.Send(cancellation.Token).WaitAsync(Guard))).Should().ThrowAsync<OperationCanceledException>();
        session.Written.Should().Equal("halt", "hello");

        // The fence's answer was taken, so the next halt needs no fence.
        session.Device.ScheduleResponseTimeout = (source, timeout) => source.CancelAfter(timeout);
        (await session.Send().WaitAsync(Guard)).Status.Should().Be(SlStatus.OK);
        session.Written.Should().Equal("halt", "hello", "halt");
    }

    // Granted the lock as its caller cancelled, a no_return command was still written: nothing read
    // the token once the wait for the lock had ended.
    [Fact]
    public async Task SendCommandAsync_NoReturnCancelledAsTheLockIsGranted_IsNotWritten()
    {
        using var session = new Session();
        using var cancellation = new CancellationTokenSource();
        // Cancelled once the lock is granted, before anything else reads the token.
        session.Device.LockGranted = cancellation.Cancel;
        await ((Func<Task>)(() => session.Device.SendCommandAsync("bt", "system", "reset", ct: cancellation.Token).WaitAsync(Guard)))
            .Should().ThrowAsync<OperationCanceledException>();
        session.Written.Should().BeEmpty();

        session.Device.LockGranted = null;
        (await session.Device.SendCommandAsync("bt", "system", "reset").WaitAsync(Guard)).Status.Should().Be(SlStatus.OK);
        session.Written.Should().Equal("reset");
    }

    [Fact]
    public async Task SendCommandAsync_SendFailureAndNoReturn_LeaveLockUsable()
    {
        using var session = new Session();
        session.Port.OnWrite = _ => throw new IOException("injected send failure");
        await ((Func<Task>)(() => session.Send())).Should().ThrowAsync<IOException>().WithMessage("injected send failure");
        session.Port.OnWrite = null;
        (await session.Device.SendCommandAsync("bt", "system", "reset").WaitAsync(Guard)).Status.Should().Be(SlStatus.OK);
        session.Port.OnWrite = _ => session.Port.Inject(session.Reply());
        (await session.Send().WaitAsync(Guard)).Status.Should().Be(SlStatus.OK);
    }

    [Fact]
    public async Task SendCommandAsync_TimeoutThenDifferentHeader_IgnoresStaleResponse()
    {
        using var session = new Session();
        session.Device.ScheduleResponseTimeout = (source, _) => source.Cancel();
        (await session.Send().WaitAsync(Guard)).Status.Should().Be(SlStatus.Timeout);
        session.Device.ScheduleResponseTimeout = (source, timeout) => source.CancelAfter(timeout);
        session.Port.OnWrite = _ => session.Port.Inject([
            .. session.Reply((ushort)SlStatus.Busy),
            .. Wire.Response(session.Device.Definitions, "set_tx_power")]);
        var next = await session.Device.SendCommandAsync("bt", "system", "set_tx_power").WaitAsync(Guard);
        next.Status.Should().Be(SlStatus.OK);
        next.Parameters.Should().ContainKeys("set_min", "set_max");
        // A response to a different header proves the NCP drained, so the later repeat needs no fence.
        session.Port.OnWrite = written => session.Port.Inject(session.ReplyTo(written));
        (await session.Send().WaitAsync(Guard)).Status.Should().Be(SlStatus.OK);
        session.Written.Should().Equal("halt", "set_tx_power", "halt");
    }

    // BGAPI headers carry no request identity, so a late response to timed-out A used to complete
    // the repeat B with A's status. The NCP answers in order: a fence command sent before B drains
    // A's late response, which is dropped as not matching the fence.
    [Fact]
    public async Task SendCommandAsync_LateSameHeaderResponse_IsDrainedByFenceBeforeTheRepeat()
    {
        using var session = new Session();
        session.Device.ScheduleResponseTimeout = (source, _) => source.Cancel();
        (await session.Send().WaitAsync(Guard)).Status.Should().Be(SlStatus.Timeout);
        session.Device.ScheduleResponseTimeout = (source, timeout) => source.CancelAfter(timeout);
        bool late = true;
        session.Port.OnWrite = written =>
        {
            if (late) session.Port.Inject(session.Reply((ushort)SlStatus.Busy)); // A's late reply comes first.
            late = false;
            session.Port.Inject(session.ReplyTo(written));
        };
        (await session.Send().WaitAsync(Guard)).Status.Should().Be(SlStatus.OK);
        session.Written.Should().Equal("halt", "hello", "halt");
    }

    // Every fence command used to be ruled out once it had gone unanswered itself, so a few retries
    // during an outage left no fence to send and the command returned Timeout unsent forever.
    [Theory]
    [InlineData("halt")]
    [InlineData("hello")]
    [InlineData("get_version")]
    public async Task SendCommandAsync_RetriesThroughOutage_SucceedOnceTheNcpAnswersAgain(string command)
    {
        using var session = new Session();
        session.Device.ScheduleResponseTimeout = (source, _) => source.Cancel();
        for (int i = 0; i < 5; i++)
            (await session.Device.SendCommandAsync("bt", "system", command).WaitAsync(Guard)).Status.Should().Be(SlStatus.Timeout);
        session.Device.ScheduleResponseTimeout = (source, timeout) => source.CancelAfter(timeout);
        session.Port.OnWrite = written => session.Port.Inject(session.ReplyTo(written));
        (await session.Device.SendCommandAsync("bt", "system", command).WaitAsync(Guard)).Status.Should().Be(SlStatus.OK);
        session.Written[^1].Should().Be(command);
    }

    // A fence whose own header went unanswered may be answered by that earlier send. That still
    // proves every send before it, which is enough here: the late answers all precede the fence's.
    [Fact]
    public async Task SendCommandAsync_LateAnswersAfterOutage_DrainBeforeTheRepeat()
    {
        using var session = new Session();
        session.Device.ScheduleResponseTimeout = (source, _) => source.Cancel();
        for (int i = 0; i < 3; i++) (await session.Send().WaitAsync(Guard)).Status.Should().Be(SlStatus.Timeout);
        session.Written.Should().Equal("halt", "hello", "get_version");
        session.Device.ScheduleResponseTimeout = (source, timeout) => source.CancelAfter(timeout);
        bool late = true;
        session.Port.OnWrite = written =>
        {
            if (late)
            {
                // The NCP recovers and answers halt (Busy), hello and get_version late, in order.
                foreach (var earlier in session.Port.Writes.Take(3))
                    session.Port.Inject(Wire.ResponseTo(session.Device.Definitions, earlier, (ushort)SlStatus.Busy));
                late = false;
            }
            session.Port.Inject(session.ReplyTo(written));
        };
        (await session.Send().WaitAsync(Guard)).Status.Should().Be(SlStatus.OK);
        session.Written.Should().Equal("halt", "hello", "get_version", "get_version", "halt");
    }

    // get_version's late answer satisfies the first fence, which proves only hello passed: halt was
    // sent after get_version, so its late Busy is still to come and a second fence must drain it.
    [Fact]
    public async Task SendCommandAsync_AmbiguousFenceAnswer_ProvesOnlyTheSendsBeforeIt()
    {
        using var session = new Session();
        session.Device.ScheduleResponseTimeout = (source, _) => source.Cancel();
        foreach (var command in new[] { "hello", "get_version", "halt" })
            (await session.Device.SendCommandAsync("bt", "system", command).WaitAsync(Guard)).Status.Should().Be(SlStatus.Timeout);
        session.Device.ScheduleResponseTimeout = (source, timeout) => source.CancelAfter(timeout);
        var sent = session.Port.Writes.ToArray();
        byte[] Late(int index) => Wire.ResponseTo(session.Device.Definitions, sent[index], (ushort)SlStatus.Busy);
        int writes = 0;
        session.Port.OnWrite = written =>
        {
            // In NCP order, but halt's late Busy waits for the next write, so a repeat sent too
            // early is already waiting for it.
            switch (++writes)
            {
                case 1: session.Port.Inject([.. Late(0), .. Late(1)]); break;
                case 2: session.Port.Inject([.. Late(2), .. session.ReplyTo(session.Port.Writes.ElementAt(3)), .. session.ReplyTo(written)]); break;
                default: session.Port.Inject(session.ReplyTo(written)); break;
            }
        };
        (await session.Send().WaitAsync(Guard)).Status.Should().Be(SlStatus.OK);
        session.Written.Should().Equal("hello", "get_version", "halt", "get_version", "hello", "halt");
    }

    // The timeout used to cancel only the wait. Fired as the reader completed the fence with
    // get_version's earlier late answer, it left that answer taken and the fence's own send
    // unrecorded, so hello's ambiguous answer later cleared get_version and a get_version then took
    // the fence's late Busy. The answer and the timeout now race on the one completion source.
    [Fact]
    public async Task SendCommandAsync_TimeoutRacingFenceAnswer_StillCountsTheFenceUnanswered()
    {
        CancellationTokenSource? fenceTimeout = null;
        using var captured = new ManualResetEventSlim();
        int armed = 0;
        using var session = new Session(new HookLogger(text =>
        {
            // On the reader, just before it completes the pending fence. The answer is injected as
            // the fence is written, so it can be routed before the fence's timeout is scheduled.
            if (text == "Completing pending response TCS" && Interlocked.Exchange(ref armed, 0) == 1)
            {
                if (!captured.Wait(Guard)) throw new TimeoutException("fence timeout was never scheduled");
                fenceTimeout!.Cancel();
            }
        }));
        var sent = new List<byte[]>();
        byte[] Late(int index, SlStatus status = SlStatus.OK) =>
            Wire.ResponseTo(session.Device.Definitions, sent[index], (ushort)status);
        session.Port.OnWrite = written =>
        {
            sent.Add(written);
            switch (sent.Count)
            {
                case 3: session.Port.Inject(Late(0)); break; // the get_version fence gets get_version's late answer
                case 4: session.Port.Inject(Late(1)); break; // the hello fence gets hello's late answer
                case 5: session.Port.Inject([.. Late(2, SlStatus.Busy), .. Late(3), .. session.ReplyTo(written)]); break;
                case > 5: session.Port.Inject(session.ReplyTo(written)); break;
            }
        };
        Task<BgapiCommandResponse> Send(string command) => session.Device.SendCommandAsync("bt", "system", command);

        session.Device.ScheduleResponseTimeout = (source, _) => source.Cancel();
        (await Send("get_version").WaitAsync(Guard)).Status.Should().Be(SlStatus.Timeout);
        (await Send("hello").WaitAsync(Guard)).Status.Should().Be(SlStatus.Timeout);

        // Repeating hello sends a get_version fence, whose timeout fires as its answer is routed.
        // Guard bounds the fence should the reader never fire it.
        session.Device.ScheduleResponseTimeout = (source, _) =>
        {
            fenceTimeout = source;
            source.CancelAfter(Guard);
            captured.Set();
        };
        armed = 1;
        (await Send("hello").WaitAsync(Guard)).Status.Should().Be(SlStatus.Timeout);

        session.Device.ScheduleResponseTimeout = (source, timeout) => source.CancelAfter(timeout);
        (await Send("get_version").WaitAsync(Guard)).Status.Should().Be(SlStatus.OK);
        session.Written.Should().Equal("get_version", "hello", "get_version", "hello", "hello", "get_version");
    }

    [Fact]
    public async Task SendCommandAsync_FenceUnanswered_ReturnsTimeoutWithoutSendingTheRepeat()
    {
        using var session = new Session();
        session.Device.ScheduleResponseTimeout = (source, _) => source.Cancel();
        (await session.Send().WaitAsync(Guard)).Status.Should().Be(SlStatus.Timeout);
        (await session.Send().WaitAsync(Guard)).Status.Should().Be(SlStatus.Timeout);
        session.Written.Should().Equal("halt", "hello");
        // The unanswered hello cannot fence itself, so the next attempt fences with get_version.
        session.Device.ScheduleResponseTimeout = (source, timeout) => source.CancelAfter(timeout);
        session.Port.OnWrite = written => session.Port.Inject(session.ReplyTo(written));
        (await session.Send().WaitAsync(Guard)).Status.Should().Be(SlStatus.OK);
        session.Written.Should().Equal("halt", "hello", "get_version", "halt");
    }

    [Fact]
    public async Task SendCommandAsync_ResponseBeforeTimeoutRegistration_CompletesExactlyOnce()
    {
        using var routed = new ManualResetEventSlim();
        using var session = new Session(new HookLogger(text =>
        {
            // The next read is started only after response routing has returned.
            if (text.Contains("Completing pending response")) routed.Set();
        }));
        session.Device.ScheduleResponseTimeout = (_, _) => { };
        session.Port.OnWrite = _ => session.Port.Inject([.. session.Reply(), .. session.Reply((ushort)SlStatus.Busy)]);
        (await session.Send().WaitAsync(Guard)).Status.Should().Be(SlStatus.OK);
        routed.IsSet.Should().BeTrue();
    }

    [Fact]
    public async Task Close_PendingCommand_CompletesUnderCallerCancellation()
    {
        using var session = new Session();
        using var cancellation = new CancellationTokenSource();
        var pending = session.Send(cancellation.Token);
        session.Device.Close();
        cancellation.Cancel();
        await ((Func<Task>)(() => pending.WaitAsync(Guard))).Should().ThrowAsync<OperationCanceledException>();
        session.Device.IsOpen.Should().BeFalse();
    }

    // Dispose used to dispose the command lock while SendCommandAsync still held it: the holder's
    // Release threw ObjectDisposedException over its cancellation, and a caller already waiting for
    // the lock was never released.
    [Fact]
    public async Task Dispose_PendingAndWaitingCommands_CompleteWithCancellationAndObjectDisposed()
    {
        using var session = new Session();
        using var cancellation = new CancellationTokenSource();
        var pending = session.Send(cancellation.Token);
        var waiting = session.Send();
        session.Device.Dispose();
        cancellation.Cancel();
        await ((Func<Task>)(() => pending.WaitAsync(Guard))).Should().ThrowAsync<OperationCanceledException>();
        await ((Func<Task>)(() => waiting.WaitAsync(Guard))).Should().ThrowAsync<ObjectDisposedException>()
            .Where(e => e.ObjectName == typeof(BgapiDevice).FullName);
        await ((Func<Task>)(() => session.Send().WaitAsync(Guard))).Should().ThrowAsync<ObjectDisposedException>()
            .Where(e => e.ObjectName == typeof(BgapiDevice).FullName);
        session.Port.Writes.Should().ContainSingle();
    }

    [Fact]
    public void Open_FailedThenRepeatedOpen_DisposesFailedPortAndRejectsSecondOpen()
    {
        using var failed = new SimulatedConnection { OnOpen = () => throw new IOException("injected open failure") };
        using var good = new SimulatedConnection();
        int calls = 0;
        var options = Options.Create(new CsharpBgapiOptions());
        using var device = new BgapiDevice(options, new BgapiConnector(options, NullLogger.Instance, (_, _) => ++calls == 1 ? failed : good));
        device.Invoking(d => d.Open("simulated")).Should().Throw<IOException>().WithMessage("injected open failure");
        failed.Disposed.Should().BeTrue();
        device.Open("simulated");
        device.Invoking(d => d.Open("simulated")).Should().Throw<InvalidOperationException>().WithMessage("*already open*");
        calls.Should().Be(2);
    }

    // The reader checked cancellation only before ReadMessage, and StopReader's join is bounded, so a
    // reader held up after a read (here in the logger, as a slow subscriber would hold it) outlived
    // Close and dispatched the old port's event into the reopened session beside the new reader.
    [Fact]
    public async Task Open_OldReaderStillRunning_ThrowsAndOldEventIsNeverDispatched()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var exited = new ManualResetEventSlim();
        using var oldPort = new SimulatedConnection();
        using var newPort = new SimulatedConnection();
        int calls = 0;
        var options = Options.Create(new CsharpBgapiOptions { StopReaderTimeoutSeconds = 0.2 });
        var logger = new HookLogger(text =>
        {
            if (text.StartsWith("Received message:") && !entered.IsSet)
            {
                entered.Set();
                if (!release.Wait(Guard)) throw new TimeoutException("test dispatch guard");
            }
            if (text == "ReaderLoop exited" && entered.IsSet) exited.Set();
        });
        using var device = new BgapiDevice(options,
            new BgapiConnector(options, NullLogger.Instance, (_, _) => ++calls == 1 ? oldPort : newPort), logger);
        device.LoadDefaultXapis();
        var delivered = Signal<int>();
        device.Subscribe("bt_evt_user_message_to_host", message => delivered.TrySetResult(Wire.Sequence(message)));
        device.Open("old");
        oldPort.Inject(Wire.Event(device.Definitions, 42));
        try
        {
            entered.Wait(Guard).Should().BeTrue();
            device.Close();
            device.Invoking(d => d.Open("new")).Should().Throw<InvalidOperationException>().WithMessage("*reader*still running*");
        }
        finally { release.Set(); }
        exited.Wait(Guard).Should().BeTrue();
        device.Open("new");
        newPort.Inject(Wire.Event(device.Definitions, 43));
        (await delivered.Task.WaitAsync(Guard)).Should().Be(43);
        Wire.Sequence(device.EventQueue.GetEvent(Guard)!).Should().Be(43);
        device.EventQueue.Count.Should().Be(0);
        calls.Should().Be(2);
    }

    // Open's IsOpen check and the connector's publication were not atomic: two concurrent callers
    // both passed the check, both opened a port, and the second overwrote the first's port and reader.
    [Fact]
    public async Task Open_ConcurrentCallers_SecondWaitsThenSeesAlreadyOpen()
    {
        using var firstOpening = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var first = new SimulatedConnection();
        using var second = new SimulatedConnection();
        first.OnOpen = () =>
        {
            firstOpening.Set();
            if (!release.Wait(Guard)) throw new TimeoutException("concurrent open guard");
        };
        int calls = 0;
        var options = Options.Create(new CsharpBgapiOptions());
        using var device = new BgapiDevice(options, new BgapiConnector(options, NullLogger.Instance,
            (_, _) => Interlocked.Increment(ref calls) == 1 ? first : second));
        var one = Task.Run(() => device.Open("one"));
        firstOpening.Wait(Guard).Should().BeTrue();
        var two = Task.Run(() => device.Open("two"));
        // Unserialized, the second caller opens its own port within this window.
        await Task.WhenAny(two, Task.Delay(200));
        release.Set();
        await one.WaitAsync(Guard);
        await ((Func<Task>)(() => two.WaitAsync(Guard))).Should().ThrowAsync<InvalidOperationException>().WithMessage("*already open*");
        first.IsOpen.Should().BeTrue();
        second.IsOpen.Should().BeFalse();
        calls.Should().Be(1);
    }

    [Fact]
    public async Task Reader_ThrowingAndReentrantSubscriptions_DoNotStopSiblings()
    {
        using var session = new Session();
        var received = Signal<int>();
        const string name = "bt_evt_user_message_to_host";
        Action<BgapiMessage> handler = _ => { };
        session.Device.Subscribe(name, _ => throw new InvalidOperationException("injected handler failure"));
        session.Device.Subscribe(name, message =>
        {
            session.Device.Subscribe(name, handler);
            session.Device.Unsubscribe(name, handler);
            received.TrySetResult(Wire.Sequence(message));
        });
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 100; i++) { session.Device.Subscribe(name, handler); session.Device.Unsubscribe(name, handler); }
        }))).WaitAsync(Guard);
        session.Port.Inject(Wire.Event(session.Device.Definitions, 17));
        (await received.Task.WaitAsync(Guard)).Should().Be(17);
    }

    // Release validation finding F5: a subscriber callback runs inline on the device's only reader
    // thread, so while it runs nothing is read off the port. A slow callback held every reply and
    // event behind it, and a callback that sent a command expecting a reply synchronously got
    // Timeout, because its reply could only be read once the callback returned. These facts
    // were tagged KnownLimitation as confirmed failures. For 1.1.0 the behaviour is accepted as the
    // documented Subscribe contract instead of moving callbacks to a dispatcher, which would break
    // callers that rely on a callback running in order with the replies around it. The facts now
    // pin that contract: what a blocking callback costs, and that the offload the docs give works.
    [Fact]
    public async Task Reader_SlowSubscriber_HoldsTheReaderUntilItReturns()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var session = new Session();
        session.Device.Subscribe("bt_evt_user_message_to_host", _ =>
        {
            entered.Set();
            if (!release.Wait(Guard)) throw new TimeoutException("slow handler guard");
        });
        session.Port.Inject(Wire.Event(session.Device.Definitions, 1));
        entered.Wait(Guard).Should().BeTrue();
        int reads = session.Port.ReadCalls;
        session.Port.OnWrite = _ => session.Port.Inject(session.Reply());
        var command = session.Send();
        try
        {
            command.IsCompleted.Should().BeFalse();
            session.Port.ReadCalls.Should().Be(reads);
        }
        finally { release.Set(); }
        (await command.WaitAsync(Guard)).Status.Should().Be(SlStatus.OK);
    }

    [Fact]
    public async Task Reader_SynchronousCommandInCallback_TimesOutBecauseItsReplyIsNeverRead()
    {
        using var session = new Session();
        var timer = Signal<CancellationTokenSource>();
        var result = Signal<SlStatus>();
        session.Device.ScheduleResponseTimeout = (source, _) => timer.TrySetResult(source);
        session.Port.OnWrite = _ => session.Port.Inject(session.Reply());
        session.Device.Subscribe("bt_evt_user_message_to_host", _ =>
            result.TrySetResult(session.Device.SendCommand("bt", "system", "halt").Status));
        session.Port.Inject(Wire.Event(session.Device.Definitions, 1));
        var timeout = await timer.Task.WaitAsync(Guard);
        result.Task.IsCompleted.Should().BeFalse();
        timeout.Cancel();
        (await result.Task.WaitAsync(Guard)).Should().Be(SlStatus.Timeout);
    }

    [Fact]
    public async Task Reader_NoReturnCommandInCallback_IsWrittenAndReturnsOk()
    {
        using var session = new Session();
        var result = Signal<SlStatus>();
        // bt.system.reboot is no_return in sl_bt.xapi: it waits for no response, so the reader is not needed.
        session.Device.Subscribe("bt_evt_user_message_to_host", _ =>
            result.TrySetResult(session.Device.SendCommand("bt", "system", "reboot").Status));
        session.Port.Inject(Wire.Event(session.Device.Definitions, 1));
        (await result.Task.WaitAsync(Guard)).Should().Be(SlStatus.OK);
        session.Written.Should().Equal("reboot");
    }

    [Fact]
    public async Task Reader_CallbackOffloadingItsCommand_GetsTheReply()
    {
        using var session = new Session();
        var result = Signal<SlStatus>();
        // No timeout ever fires, so OK can only come from the reader routing the reply.
        session.Device.ScheduleResponseTimeout = (_, _) => { };
        session.Port.OnWrite = _ => session.Port.Inject(session.Reply());
        session.Device.Subscribe("bt_evt_user_message_to_host", _ =>
            Task.Run(() => result.TrySetResult(session.Device.SendCommand("bt", "system", "halt").Status)));
        session.Port.Inject(Wire.Event(session.Device.Definitions, 1));
        (await result.Task.WaitAsync(Guard)).Should().Be(SlStatus.OK);
    }

    [Fact]
    public void Reader_CloseFromCallback_ClosesThePortAndOpenThrowsUntilTheCallbackReturns()
    {
        using var closed = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var exited = new ManualResetEventSlim();
        using var oldPort = new SimulatedConnection();
        using var newPort = new SimulatedConnection();
        int calls = 0, notStopped = 0;
        var options = Options.Create(new CsharpBgapiOptions { StopReaderTimeoutSeconds = 0.2 });
        var logger = new HookLogger(text =>
        {
            if (text.StartsWith("BgapiDevice reader did not stop")) Interlocked.Increment(ref notStopped);
            if (text == "ReaderLoop exited" && closed.IsSet) exited.Set();
        });
        using var device = new BgapiDevice(options,
            new BgapiConnector(options, NullLogger.Instance, (_, _) => ++calls == 1 ? oldPort : newPort), logger);
        device.LoadDefaultXapis();
        device.Subscribe("bt_evt_user_message_to_host", _ =>
        {
            // Close cannot join the reader: it is this callback's own thread.
            device.Close();
            closed.Set();
            if (!release.Wait(Guard)) throw new TimeoutException("close callback guard");
        });
        device.Open("old");
        oldPort.Inject(Wire.Event(device.Definitions, 1));
        try
        {
            closed.Wait(Guard).Should().BeTrue();
            notStopped.Should().Be(1);
            oldPort.IsOpen.Should().BeFalse();
            device.Invoking(d => d.Open("new")).Should().Throw<InvalidOperationException>().WithMessage("*reader*still running*");
        }
        finally { release.Set(); }
        exited.Wait(Guard).Should().BeTrue();
        device.Open("new");
        device.IsOpen.Should().BeTrue();
        calls.Should().Be(2);
    }

    [Fact]
    public void Reader_DisposeFromCallback_ClosesThePortAndTheReaderExitsWhenTheCallbackReturns()
    {
        using var disposed = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var exited = new ManualResetEventSlim();
        int notStopped = 0;
        using var session = new Session(new HookLogger(text =>
        {
            if (text.StartsWith("BgapiDevice reader did not stop")) Interlocked.Increment(ref notStopped);
            if (text == "ReaderLoop exited" && disposed.IsSet) exited.Set();
        }), stopSeconds: 0.2);
        session.Device.Subscribe("bt_evt_user_message_to_host", _ =>
        {
            // Dispose, like Close, cannot join the reader: it is this callback's own thread.
            session.Device.Dispose();
            disposed.Set();
            if (!release.Wait(Guard)) throw new TimeoutException("dispose callback guard");
        });
        session.Port.Inject(Wire.Event(session.Device.Definitions, 1));
        try
        {
            disposed.Wait(Guard).Should().BeTrue();
            notStopped.Should().Be(1);
            session.Port.IsOpen.Should().BeFalse();
            exited.IsSet.Should().BeFalse("the reader is still inside the callback");
        }
        finally { release.Set(); }
        exited.Wait(Guard).Should().BeTrue();
    }

    // A caller on a single-threaded SynchronizationContext (a WinForms or WPF UI thread) that called
    // the sync SendCommand never got it back. The library's awaits captured that context, so the
    // awaited response posted its continuation to the very thread blocked in GetResult, and the
    // response timeout could not break it: its continuation needed the same thread. RetryUntilAsync
    // awaited its command, its retry delay and its event waits the same way.

    [Fact]
    public void SendCommand_OnASingleThreadedContext_ReturnsTheResponse()
    {
        using var session = new Session();
        session.Port.OnWrite = _ => session.Port.Inject(session.Reply());
        OnBlockedContext(() => session.Device.SendCommand("bt", "system", "halt").Status).Should().Be(SlStatus.OK);
    }

    [Fact]
    public void RetryUntilAsync_BlockedOnASingleThreadedContext_Completes()
    {
        using var session = new Session();
        int attempts = 0;
        session.Port.OnWrite = _ =>
        {
            int attempt = ++attempts;
            session.Port.Inject([.. Wire.Event(session.Device.Definitions, attempt),
                .. session.Reply((ushort)(attempt == 1 ? SlStatus.Busy : SlStatus.OK))]);
        };
        // A non-zero interval, so the retry delay is a real await rather than a completed task.
        var events = OnBlockedContext(() => session.Device.RetryUntilAsync(() => session.Send(),
            new CallableSelector((message, _) => Wire.Sequence(message) == 2 ? EventCategory.SelectFinal : EventCategory.Ignore),
            new RetryParams { RetryMax = 1, RetryCmdMax = 2, RetryCmdInterval = TimeSpan.FromMilliseconds(1), RetryInterval = Guard })
            .GetAwaiter().GetResult());
        events.Select(Wire.Sequence).Should().Equal(2);
        attempts.Should().Be(2);
    }

    // Runs call on a thread whose SynchronizationContext drops every post, as a UI thread blocked in a
    // synchronous call never runs one. Throws when call has not returned within Guard.
    private static T OnBlockedContext<T>(Func<T> call)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new BlockedContext());
            try { result = call(); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.Start();
        if (!thread.Join(Guard))
            throw new TimeoutException("the call never returned: a continuation was posted to its blocked thread");
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        return result;
    }

    private sealed class BlockedContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) { }
    }

    // LogDebug(template, args) builds its params array and boxes every value argument before the
    // logger is asked whether Debug is on. With Debug off, the reader still paid that for every frame,
    // every dispatched event and every command, and every WaitEvents call paid it too.
    [Fact]
    public async Task HotPaths_DebugDisabled_MakeNoDebugCallWithArguments()
    {
        var logger = new DebugOffLogger();
        var options = Options.Create(new CsharpBgapiOptions { MaxQueuedEvents = 1 });
        using var port = new SimulatedConnection();
        using var device = new BgapiDevice(options, new BgapiConnector(options, logger, (_, _) => port), logger);
        device.LoadDefaultXapis();
        device.Open("simulated");
        var third = Signal<bool>();
        device.Subscribe("bt_evt_user_message_to_host", message =>
        {
            if (Wire.Sequence(message) == 3) third.TrySetResult(true);
        });
        port.OnWrite = written => port.Inject(Wire.ResponseTo(device.Definitions, written));

        (await device.SendCommandAsync("bt", "system", "halt").WaitAsync(Guard)).Status.Should().Be(SlStatus.OK);
        // 0xFF carries device id 15, which no loaded API has: one resync episode, so every drop after
        // the first logs at Debug.
        port.Inject([0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
        port.Inject([0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
        foreach (var sequence in new[] { 1, 2, 3 })
            port.Inject(Wire.Event(device.Definitions, sequence));
        await third.Task.WaitAsync(Guard);
        device.WaitEvents(new CallableSelector((message, _) =>
                Wire.Sequence(message) == 3 ? EventCategory.SelectFinal : EventCategory.Ignore), Guard)
            .Select(Wire.Sequence).Should().Equal(3);

        device.DroppedByteCount.Should().Be(10);
        device.EventQueue.DroppedEventCount.Should().Be(2, "a one-event queue evicts twice, the second time at Debug");
        logger.DebugWithArguments.Should().BeEmpty();
    }

    // Reports Debug as off, as a production host does, and records each Debug or Trace call that
    // still arrives carrying arguments.
    private sealed class DebugOffLogger : ILogger
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> DebugWithArguments { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level > LogLevel.Debug;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            // The state lists {OriginalFormat} after the arguments, so more than one entry means arguments.
            if (level <= LogLevel.Debug && state is IReadOnlyList<KeyValuePair<string, object?>> { Count: > 1 })
                DebugWithArguments.Enqueue(formatter(state, exception));
        }
    }

    internal sealed class HookLogger(Action<string> hook) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => hook(formatter(state, exception));
    }
}
