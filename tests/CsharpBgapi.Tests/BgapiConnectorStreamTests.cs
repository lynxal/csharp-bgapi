using CsharpBgapi.Protocol;
using CsharpBgapi.Serial;
using CsharpBgapi.Validation;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CsharpBgapi.Tests;

public class BgapiConnectorStreamTests
{
    private static readonly XapiDefinitions Definitions = Wire.LoadDefinitions();
    private static readonly BgapiProtocol Protocol = new(Definitions);

    // Close used to leave the outstanding read unabandoned; only a reopen abandoned it, so a final
    // close let the read's late driver fault surface as an unobserved task exception.
    [Fact]
    public void Close_LateReadFault_IsObserved()
    {
        string marker = "final-close-fault-" + Guid.NewGuid();
        UnobservedFaults.Raised(marker, () => CloseWithReadPendingThenFault(marker)).Should().BeFalse();
    }

    private static void CloseWithReadPendingThenFault(string marker)
    {
        var read = new TaskCompletionSource<int>();
        var port = new SimulatedConnection { ControlledRead = read.Task };
        var connector = new BgapiConnector(Options.Create(new CsharpBgapiOptions()), NullLogger.Instance, (_, _) => port);
        connector.Open("simulated");
        connector.ReadMessage(Protocol, TimeSpan.Zero).Should().BeNull();
        connector.Close();
        read.SetException(new IOException(marker));
    }

    [Fact]
    public void ReadMessage_SeededSplitsCoalescingAndPolls_PreservesEveryByteAndFrame()
    {
        foreach (int seed in new[] { 17, 20261023, 8675309 })
        {
            using var port = new SimulatedConnection();
            using var connector = new BgapiConnector(Options.Create(new CsharpBgapiOptions()), NullLogger.Instance, (_, _) => port);
            // Reads complete on a pool thread while this one polls; under parallel test load a
            // partial frame could otherwise outlast the 500 ms default and lose a byte.
            connector.StalledCandidateBudget = TimeSpan.FromMinutes(1);
            connector.SetKnownDeviceIds(Definitions.GetKnownDeviceIds());
            connector.Open("simulated");
            var frames = Enumerable.Range(0, 256).Select(i => i % 3 == 0
                ? Wire.Response(Definitions, status: (ushort)i) : Wire.Event(Definitions, i, i % 2 == 0 ? 255 : 16)).ToArray();
            var bytes = frames.SelectMany(f => f).ToArray();
            var random = new Random(seed);
            var received = new List<byte[]>();
            for (int offset = 0; offset < bytes.Length;)
            {
                int size = Math.Min(random.Next(1, 4097), bytes.Length - offset);
                port.Inject(bytes.AsSpan(offset, size).ToArray());
                offset += size;
                Drain();
            }
            // A finite guard diagnoses stalled completion without hanging the suite.
            var guard = System.Diagnostics.Stopwatch.StartNew();
            while (received.Count < frames.Length && guard.Elapsed < TimeSpan.FromSeconds(10)) Drain();
            received.SelectMany(f => f).Should().Equal(bytes, $"seed {seed}");
            received.Should().HaveCount(frames.Length);
            connector.DroppedByteCount.Should().Be(0);
            port.OverlappingReads.Should().Be(0);
            port.CancellableReads.Should().Be(0);

            void Drain()
            {
                BgapiMessage? message;
                while ((message = connector.ReadMessage(Protocol, TimeSpan.Zero)) is not null)
                    received.Add([.. message.Header.ToBytes(), .. message.Payload.ToArray()]);
            }
        }
    }

    [Fact]
    public void ReadMessage_EverySplitOfLargestUint8ArrayEvent_PreservesPartialFrame()
    {
        var frame = Wire.Event(Definitions, 73, 255);
        for (int split = 1; split < frame.Length; split++)
        {
            using var connector = new BgapiConnector { StalledCandidateBudget = TimeSpan.FromMinutes(1) };
            connector.AppendReceived(frame.AsSpan(0, split));
            connector.TakeBufferedFrame(Protocol).Should().BeNull();
            connector.AppendReceived(frame.AsSpan(split));
            var message = connector.TakeBufferedFrame(Protocol);
            message.Should().NotBeNull();
            message!.Payload.ToArray().Should().Equal(frame.AsSpan(4).ToArray());
            connector.TakeBufferedFrame(Protocol).Should().BeNull();
            connector.DroppedByteCount.Should().Be(0);
        }
    }

    [Fact]
    public void TakeBufferedFrame_MaximumWireLength_AllSplitBoundariesPreserveContents()
    {
        var definition = Definitions.GetClass("bt", "gatt").Events.Single(e => e.Name == "characteristic_value");
        // connection:uint8, characteristic:uint16, att_opcode:uint8, offset:uint16,
        // value:uint16array. 6 fixed bytes + 2 prefix bytes + 2039 value bytes = 2047.
        var payload = new byte[2047];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6), 2039);
        for (int i = 8; i < payload.Length; i++) payload[i] = (byte)i;
        var header = new BgapiHeader((byte)(0x80 | Definitions.GetDeviceId("bt") << 3),
            2047, definition.ClassIndex, definition.Index);
        byte[] frame = [.. header.ToBytes(), .. payload];
        Protocol.IsKnownHeader(header).Should().BeTrue();
        for (int split = 1; split < frame.Length; split++)
        {
            using var connector = new BgapiConnector { StalledCandidateBudget = TimeSpan.FromMinutes(1) };
            connector.AppendReceived(frame.AsSpan(0, split));
            connector.TakeBufferedFrame(Protocol).Should().BeNull();
            connector.AppendReceived(frame.AsSpan(split));
            connector.TakeBufferedFrame(Protocol)!.Payload.ToArray().Should().Equal(payload);
            connector.DroppedByteCount.Should().Be(0);
        }
    }

    [Fact]
    public void ReadMessage_DriverFaultAndEof_AreObservableAndNextFrameSurvives()
    {
        using var port = new SimulatedConnection();
        using var connector = new BgapiConnector(Options.Create(new CsharpBgapiOptions()), NullLogger.Instance, (_, _) => port);
        connector.Open("simulated");
        port.Fault(new IOException("injected read failure"));
        connector.Invoking(c => c.ReadMessage(Protocol, TimeSpan.FromSeconds(1))).Should()
            .Throw<IOException>().WithMessage("injected read failure");
        port.Inject([]);
        connector.ReadMessage(Protocol, TimeSpan.FromSeconds(1)).Should().BeNull();
        port.Inject(Wire.Event(Definitions, 91));
        Wire.Sequence(connector.ReadMessage(Protocol, TimeSpan.FromSeconds(1))!).Should().Be(91);
        port.OverlappingReads.Should().Be(0);
    }

    // Open used to publish a second port over an open one, leaving the first open with nothing
    // referencing it.
    [Fact]
    public void Open_AlreadyOpen_ThrowsAndKeepsTheOpenPort()
    {
        using var first = new SimulatedConnection();
        int calls = 0;
        using var connector = new BgapiConnector(Options.Create(new CsharpBgapiOptions()), NullLogger.Instance,
            (_, _) => { calls++; return first; });
        connector.Open("first");
        connector.Invoking(c => c.Open("second")).Should().Throw<InvalidOperationException>().WithMessage("*already open*");
        calls.Should().Be(1);
        first.IsOpen.Should().BeTrue();
    }

    [Fact]
    public void ReadMessage_Reopen_AbandonsPendingReadWithoutLeakingOldBytes()
    {
        using var old = new SimulatedConnection { CompleteOnClose = false };
        using var replacement = new SimulatedConnection();
        int calls = 0;
        using var connector = new BgapiConnector(Options.Create(new CsharpBgapiOptions()), NullLogger.Instance,
            (_, _) => ++calls == 1 ? old : replacement);
        connector.Open("old");
        connector.ReadMessage(Protocol, TimeSpan.Zero).Should().BeNull();
        connector.Close();
        connector.Open("new");
        old.Inject(Wire.Event(Definitions, 1));
        replacement.Inject(Wire.Event(Definitions, 2));
        Wire.Sequence(connector.ReadMessage(Protocol, TimeSpan.FromSeconds(1))!).Should().Be(2);
        connector.ReadMessage(Protocol, TimeSpan.Zero).Should().BeNull();
    }
}
