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
        var active = session.Send();
        using var cancellation = new CancellationTokenSource();
        var context = new QueuedContext();
        var waiting = context.Run(() => session.Device.SendCommandAsync("bt", "system", "reset", ct: cancellation.Token));
        session.Port.Inject(session.Reply());
        (await active.WaitAsync(Guard)).Status.Should().Be(SlStatus.OK);
        // The lock was released to the waiter, whose continuation is queued and has not run.
        cancellation.Cancel();
        context.RunUntil(waiting);
        await ((Func<Task>)(() => waiting.WaitAsync(Guard))).Should().ThrowAsync<OperationCanceledException>();
        session.Written.Should().Equal("halt");

        (await session.Device.SendCommandAsync("bt", "system", "reset").WaitAsync(Guard)).Status.Should().Be(SlStatus.OK);
        session.Written.Should().Equal("halt", "reset");
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
        // The wait's continuation is queued, so it runs after the reader has completed the fence.
        // Guard bounds the fence should the reader never fire it.
        session.Device.ScheduleResponseTimeout = (source, _) =>
        {
            fenceTimeout = source;
            source.CancelAfter(Guard);
            captured.Set();
        };
        armed = 1;
        var context = new QueuedContext();
        var raced = context.Run(() => Send("hello"));
        context.RunUntil(raced);
        (await raced.WaitAsync(Guard)).Status.Should().Be(SlStatus.Timeout);

        session.Device.ScheduleResponseTimeout = (source, timeout) => source.CancelAfter(timeout);
        (await Send("get_version").WaitAsync(Guard)).Status.Should().Be(SlStatus.OK);
        session.Written.Should().Equal("get_version", "hello", "get_version", "hello", "hello", "get_version");
    }

    // Posts continuations to a queue that only the test thread runs.
    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback, object?)> _queue = [];
        public override void Post(SendOrPostCallback callback, object? state) => _queue.Add((callback, state));
        public T Run<T>(Func<T> start)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try { return start(); }
            finally { SetSynchronizationContext(previous); }
        }
        public void RunUntil(Task task)
        {
            var guard = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && guard.Elapsed < Guard)
                if (_queue.TryTake(out var item, 10)) Run(() => { item.Item1(item.Item2); return 0; });
        }
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

    [Fact, Trait("Category", "KnownLimitation")]
    public async Task Reader_SlowSubscriber_BlocksResponseAndSerialDrainingUntilReleased()
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

    [Fact, Trait("Category", "KnownLimitation")]
    public async Task Reader_SynchronousCommandInCallback_CannotReadItsResponseBeforeTimeout()
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

    internal sealed class HookLogger(Action<string> hook) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => hook(formatter(state, exception));
    }
}
