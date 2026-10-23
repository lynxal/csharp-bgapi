using CsharpBgapi.Serial;
using CsharpBgapi.Validation;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace CsharpBgapi.Tests;

public class PendingReadTests
{
    // A read that outlives the caller's timeout must still deliver its bytes. BgapiConnector used to
    // poll SerialPort.Read with a 100 ms timeout; on Linux SerialStream.Unix reads the fd into a
    // timed-out request and then drops the bytes, because TrySetResult on an already-cancelled
    // request does nothing. Hub firmware job 196 lost query_block_status_complete at block 433 of 553
    // on 2026-10-22 and restarted a 78% transfer from block 0. PendingRead never cancels a read: a
    // timeout leaves it in flight and the next call collects it.

    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(10);

    private readonly Stream _stream = Substitute.For<Stream>();
    private readonly Queue<TaskCompletionSource<int>> _reads = new();
    private byte[]? _lastBuffer;

    public PendingReadTests()
    {
        _stream.ReadAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _lastBuffer = ci.ArgAt<byte[]>(0);
                var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                _reads.Enqueue(tcs);
                return tcs.Task;
            });
    }

    private int ReadAsyncCalls => _stream.ReceivedCalls()
        .Count(c => c.GetMethodInfo().Name == nameof(Stream.ReadAsync));

    [Fact]
    public void TryTake_ReadStillPending_ReturnsNullAndIssuesNoSecondRead()
    {
        var sut = new PendingRead(_stream, 64);

        var first = sut.TryTake(Short);
        var second = sut.TryTake(Short);

        first.Should().BeNull();
        second.Should().BeNull();
        ReadAsyncCalls.Should().Be(1, "a timeout must leave the one read in flight, never start another");
    }

    [Fact]
    public void TryTake_ReadCompletesAfterTimeout_NextCallDeliversItsBytes()
    {
        var sut = new PendingRead(_stream, 64);
        sut.TryTake(Short).Should().BeNull();

        byte[] frame = [0xA0, 0x02, 0x16, 0x09, 0x00, 0x00];
        frame.CopyTo(_lastBuffer!, 0);
        _reads.Dequeue().SetResult(frame.Length);

        var received = sut.TryTake(Short);

        received.Should().NotBeNull();
        received!.Value.ToArray().Should().Equal(frame);
        ReadAsyncCalls.Should().Be(1);
    }

    [Fact]
    public void TryTake_ReadCompletesWithNoBytes_ReturnsEmptyAndNextCallStartsFreshRead()
    {
        var sut = new PendingRead(_stream, 64);
        sut.TryTake(Short);
        _reads.Dequeue().SetResult(0);

        var received = sut.TryTake(Short);
        sut.TryTake(Short);

        received.Should().NotBeNull();
        received!.Value.Length.Should().Be(0);
        ReadAsyncCalls.Should().Be(2);
    }

    [Fact]
    public void TryTake_ReadFaults_RethrowsOnceThenStartsFreshRead()
    {
        var sut = new PendingRead(_stream, 64);
        sut.TryTake(Short);
        _reads.Dequeue().SetException(new IOException("port closed"));

        var faulted = () => sut.TryTake(Short);

        faulted.Should().Throw<IOException>().WithMessage("port closed");
        sut.TryTake(Short).Should().BeNull();
        ReadAsyncCalls.Should().Be(2);
    }

    [Fact]
    public void Abandon_ReadInFlight_NextCallStartsFreshRead()
    {
        var sut = new PendingRead(_stream, 64);
        sut.TryTake(Short);

        sut.Abandon();
        sut.TryTake(Short);

        ReadAsyncCalls.Should().Be(2);
    }

    [Fact]
    public void Abandon_LateFault_IsObserved()
    {
        string marker = "abandoned-read-fault-" + Guid.NewGuid();
        UnobservedFaults.Raised(marker, () => FaultLate(marker, abandon: true)).Should().BeFalse();
    }

    // Control for the test above: without Abandon the same fault is reported, so a false there
    // means Abandon observed it rather than that the check could not see it.
    [Fact]
    public void Dropped_LateFaultWithoutAbandon_IsUnobserved()
    {
        string marker = "dropped-read-fault-" + Guid.NewGuid();
        UnobservedFaults.Raised(marker, () => FaultLate(marker, abandon: false)).Should().BeTrue();
    }

    private static void FaultLate(string marker, bool abandon)
    {
        var read = new TaskCompletionSource<int>();
        var pending = new PendingRead(new SimulatedConnection { ControlledRead = read.Task }, 64);
        pending.TryTake(TimeSpan.Zero).Should().BeNull();
        if (abandon) pending.Abandon();
        read.SetException(new IOException(marker));
    }

    [Fact]
    public void Abandon_ReplacementSession_HasIndependentBuffer()
    {
        var old = new PendingRead(_stream, 64);
        old.TryTake(TimeSpan.Zero);
        var oldRead = _reads.Dequeue();
        var oldBuffer = _lastBuffer!;
        old.Abandon();
        var replacement = new PendingRead(_stream, 64);
        replacement.TryTake(TimeSpan.Zero);
        _lastBuffer.Should().NotBeSameAs(oldBuffer);
        oldBuffer[0] = 99;
        _lastBuffer![0] = 42;
        oldRead.SetException(new IOException("abandoned session"));
        _reads.Dequeue().SetResult(1);
        replacement.TryTake(TimeSpan.Zero)!.Value.ToArray().Should().Equal(42);
        _stream.Received(2).ReadAsync(Arg.Any<byte[]>(), 0, 64, CancellationToken.None);
    }

    [Fact]
    public void TryTake_ThousandsOfPollTimeouts_StillIssuesOnlyOneUncancelledRead()
    {
        var sut = new PendingRead(_stream, 64);
        for (int i = 0; i < 2000; i++) sut.TryTake(TimeSpan.Zero).Should().BeNull();
        _stream.Received(1).ReadAsync(Arg.Any<byte[]>(), 0, 64, CancellationToken.None);
        _reads.Dequeue().SetException(new TimeoutException("driver timeout"));
        sut.Invoking(p => p.TryTake(TimeSpan.Zero)).Should().Throw<TimeoutException>().WithMessage("driver timeout");
        sut.TryTake(TimeSpan.Zero).Should().BeNull();
        ReadAsyncCalls.Should().Be(2);
    }
}
