using CsharpBgapi.Protocol;
using CsharpBgapi.Serial;
using FluentAssertions;
using Xunit;

namespace CsharpBgapi.Tests;

public class BgapiConnectorFrameTests
{
    // A desynced serial stream must resync, not manufacture frames. ReadMessage used to read the
    // header and payload straight off the port and, on a short read, return null while DISCARDING
    // the bytes it had already consumed — leaving the port mid-frame so the next call parsed payload
    // bytes as a header. Resync validated only the device-id nibble, which accepts roughly one
    // random byte in eight. Together those produced a btmesh "response" with cls=0 idx=0 (btmesh has
    // no class 0) whose bogus payload length swallowed the real query_block_status response:
    // Hub firmware job 158 died at block 192 of 479 on 2026-09-01.

    private static readonly XapiDefinitions Definitions = LoadDefinitions();
    private static readonly BgapiProtocol Protocol = new(Definitions);

    private static XapiDefinitions LoadDefinitions()
    {
        var definitions = new XapiDefinitions();
        foreach (var resourceName in EmbeddedXapiResources.AllResourceNames)
        {
            using var stream = EmbeddedXapiResources.OpenResource(resourceName);
            definitions.LoadFromStream(stream);
        }
        return definitions;
    }

    /// <summary>The 4-byte response header for btmesh mbt_client.query_block_status — the command
    /// whose response the incident lost. Indices come from the definitions, never literals.</summary>
    private static byte[] QueryBlockStatusResponseHeader(ushort payloadLength)
    {
        var cmd = Definitions.GetCommand("btmesh", "mbt_client", "query_block_status");
        var header = BgapiHeader.CreateCommand(
            Definitions.GetDeviceId("btmesh"), cmd.ClassIndex, cmd.Index, payloadLength);
        return header.ToBytes();
    }

    /// <summary>The 4-byte response header for btmesh mbt_client.get_status, whose returns are
    /// errorcode + uint8 + uint16 + uint16 — the only nearby command that declares room for a
    /// payload at all, since query_block_status returns an errorcode and nothing else.</summary>
    private static byte[] GetStatusResponseHeader(ushort payloadLength)
    {
        var cmd = Definitions.GetCommand("btmesh", "mbt_client", "get_status");
        var header = BgapiHeader.CreateCommand(
            Definitions.GetDeviceId("btmesh"), cmd.ClassIndex, cmd.Index, payloadLength);
        return header.ToBytes();
    }

    /// <summary>The exact garbage header from the incident: btmesh device-id nibble, class 0,
    /// index 0 — a device id the old nibble check accepted and a class btmesh does not have.</summary>
    private static byte[] IncidentGarbageHeader()
    {
        var header = BgapiHeader.CreateCommand(
            Definitions.GetDeviceId("btmesh"), classIndex: 0, commandIndex: 0, payloadLength: 0);
        return header.ToBytes();
    }

    private static byte[] EventFrame()
    {
        var mbtClient = Definitions.FindAllClasses("btmesh").Single(c => c.Name == "mbt_client");
        var evt = mbtClient.Events.First();
        var header = new BgapiHeader(
            (byte)(BgapiHeader.EventFlag | (Definitions.GetDeviceId("btmesh") << 3)),
            payloadLength: 0, evt.ClassIndex, evt.Index);
        return header.ToBytes();
    }

    [Fact]
    public void IsKnownHeader_BtmeshClassZero_IsRejected()
    {
        var header = BgapiHeader.Parse(IncidentGarbageHeader());

        Protocol.IsKnownHeader(header).Should().BeFalse(
            "btmesh declares no class 0, so these four bytes cannot start a frame");
    }

    [Fact]
    public void IsKnownHeader_RealMbtClientResponse_IsAccepted()
    {
        var header = BgapiHeader.Parse(QueryBlockStatusResponseHeader(payloadLength: 0));

        Protocol.IsKnownHeader(header).Should().BeTrue();
    }

    [Fact]
    public void TryExtractFrame_CompleteResponse_ReturnsFrameAndConsumesIt()
    {
        var frame = QueryBlockStatusResponseHeader(payloadLength: 0);

        var result = BgapiConnector.TryExtractFrame(frame, Protocol, knownDeviceIds: null);

        result.Message.Should().NotBeNull();
        result.Message!.IsResponse.Should().BeTrue();
        result.Message.EventName.Should().Be("btmesh_cmd_mbt_client_query_block_status_response");
        result.Consumed.Should().Be(frame.Length);
        result.DroppedBytes.Should().Be(0);
    }

    [Fact]
    public void TryExtractFrame_CompleteEvent_ReturnsFrameAndConsumesIt()
    {
        var frame = EventFrame();

        var result = BgapiConnector.TryExtractFrame(frame, Protocol, knownDeviceIds: null);

        result.Message.Should().NotBeNull();
        result.Message!.IsEvent.Should().BeTrue();
        result.Consumed.Should().Be(frame.Length);
        result.DroppedBytes.Should().Be(0);
    }

    [Fact]
    public void TryExtractFrame_FrameSplitAcrossPolls_IsReturnedWholeOnceComplete()
    {
        // The defect: the old code consumed the header, failed to read the payload, and threw the
        // header away. Here the partial frame must survive to be completed by the next poll.
        var payload = new byte[] { 0x00, 0x00, 0x01, 0x01 };
        var full = GetStatusResponseHeader((ushort)payload.Length).Concat(payload).ToArray();

        var firstPoll = BgapiConnector.TryExtractFrame(full.Take(5).ToArray(), Protocol, knownDeviceIds: null);

        firstPoll.Message.Should().BeNull("the payload has not fully arrived yet");
        firstPoll.Consumed.Should().Be(0, "a partial frame must never be discarded");
        firstPoll.DroppedBytes.Should().Be(0);

        var secondPoll = BgapiConnector.TryExtractFrame(full, Protocol, knownDeviceIds: null);

        secondPoll.Message.Should().NotBeNull();
        secondPoll.Consumed.Should().Be(full.Length);
        secondPoll.DroppedBytes.Should().Be(0);
    }

    [Fact]
    public void TryExtractFrame_IncidentGarbageHeader_DropsOneByteAndFindsFollowingFrame()
    {
        // dev=5 cls=0 idx=0 passed the old device-id-nibble check and was accepted as a frame.
        var good = QueryBlockStatusResponseHeader(payloadLength: 0);
        var buffer = IncidentGarbageHeader().Concat(good).ToArray();

        var result = BgapiConnector.TryExtractFrame(buffer, Protocol, knownDeviceIds: null);

        result.Message.Should().NotBeNull();
        result.Message!.ClassIndex.Should().NotBe((byte)0);
        result.DroppedBytes.Should().Be(4, "the four garbage bytes are dropped one at a time");
        result.Consumed.Should().Be(buffer.Length);
    }

    [Fact]
    public void TryExtractFrame_ResyncsOneByteAtATime_NotOneHeaderAtATime()
    {
        // A real frame start can sit at offset 1 of a rejected 4-byte window; skipping four bytes
        // would step over it.
        var good = QueryBlockStatusResponseHeader(payloadLength: 0);
        var buffer = new byte[] { 0xFF }.Concat(good).ToArray();

        var result = BgapiConnector.TryExtractFrame(buffer, Protocol, knownDeviceIds: null);

        result.Message.Should().NotBeNull();
        result.DroppedBytes.Should().Be(1);
        result.Consumed.Should().Be(buffer.Length);
    }

    [Fact]
    public void TryExtractFrame_JunkOnly_DropsJunkAndKeepsNothingDecodable()
    {
        var buffer = new byte[] { 0xFF, 0xFE, 0xFD, 0xFC, 0xFB };

        var result = BgapiConnector.TryExtractFrame(buffer, Protocol, knownDeviceIds: null);

        result.Message.Should().BeNull();
        result.DroppedBytes.Should().Be(2, "only the trailing 3 bytes could still start a header");
        result.Consumed.Should().Be(2);
    }

    [Fact]
    public void TryExtractFrame_TrailingBytesTooShortForHeader_AreKeptNotDropped()
    {
        var buffer = QueryBlockStatusResponseHeader(payloadLength: 0).Take(3).ToArray();

        var result = BgapiConnector.TryExtractFrame(buffer, Protocol, knownDeviceIds: null);

        result.Message.Should().BeNull();
        result.Consumed.Should().Be(0);
        result.DroppedBytes.Should().Be(0);
    }

    [Fact]
    public void TryExtractFrame_UnknownDeviceIdInPreFilter_IsSkipped()
    {
        // SetKnownDeviceIds' pre-filter still applies: a header whose device id is not in the set
        // is junk regardless of what the definitions would say.
        var good = QueryBlockStatusResponseHeader(payloadLength: 0);

        var result = BgapiConnector.TryExtractFrame(good, Protocol, knownDeviceIds: new HashSet<byte> { 4 });

        result.Message.Should().BeNull();
        result.DroppedBytes.Should().Be(1, "the btmesh header is rejected and scanning advances");
    }

    [Fact]
    public void TryExtractFrame_EmptyBuffer_ReturnsNothing()
    {
        var result = BgapiConnector.TryExtractFrame([], Protocol, knownDeviceIds: null);

        result.Message.Should().BeNull();
        result.Consumed.Should().Be(0);
        result.DroppedBytes.Should().Be(0);
    }

    // Buffering a partial frame indefinitely is its own desync: a header whose payload byte was lost
    // (or whose length bits are corrupt) is never satisfied by its own frame, so the NEXT real
    // frame's bytes get folded into it — and because the header is genuine, the fabricated result
    // can complete the in-flight command with someone else's payload. The removed ReadExact ladder
    // could at least abandon a frame; StalledCandidateBudget restores that without the discard that
    // left the stream mid-frame.

    /// <summary>A genuine header claiming 7 payload bytes, of which only 2 ever arrive.</summary>
    private static byte[] TruncatedFrame()
    {
        return [.. GetStatusResponseHeader(payloadLength: 7), 0x00, 0x00];
    }

    [Fact]
    public void TakeBufferedFrame_StalledCandidate_IsAbandonedSoTheFollowingFrameIsFound()
    {
        var truncated = TruncatedFrame();
        using var connector = new BgapiConnector();
        connector.StalledCandidateBudget = TimeSpan.Zero;
        connector.AppendReceived([.. truncated, .. QueryBlockStatusResponseHeader(payloadLength: 0)]);

        // First call notes the candidate; later calls expire it and resync onto the real frame.
        BgapiMessage? frame = null;
        for (int poll = 0; poll < 10 && frame is null; poll++)
            frame = connector.TakeBufferedFrame(Protocol);

        frame.Should().NotBeNull("the frame after the truncated one must still be delivered");
        frame!.EventName.Should().Be("btmesh_cmd_mbt_client_query_block_status_response");
        frame.Header.PayloadLength.Should().Be(0, "the delivered frame is the intact one, not the truncated one");
        connector.DroppedByteCount.Should().Be(truncated.Length,
            "exactly the truncated frame's bytes are discarded, nothing of the good frame");
    }

    [Fact]
    public void TakeBufferedFrame_CandidateWithinBudget_IsKeptForTheNextPoll()
    {
        using var connector = new BgapiConnector();
        connector.StalledCandidateBudget = TimeSpan.FromMinutes(1);
        connector.AppendReceived(TruncatedFrame());

        connector.TakeBufferedFrame(Protocol).Should().BeNull();
        connector.TakeBufferedFrame(Protocol).Should().BeNull();

        connector.DroppedByteCount.Should().Be(0,
            "an ordinary partial read must not be abandoned — that was the original defect");
    }

    [Fact]
    public void TakeBufferedFrame_AfterCandidateCompletes_ClockDoesNotLeakToTheNextFrame()
    {
        var connectorBudget = TimeSpan.FromMinutes(1);
        using var connector = new BgapiConnector();
        connector.StalledCandidateBudget = connectorBudget;

        var payload = new byte[] { 0x01, 0x02 };
        var frame = QueryBlockStatusResponseHeader((ushort)payload.Length).Concat(payload).ToArray();

        connector.AppendReceived(frame.AsSpan(0, 4));
        connector.TakeBufferedFrame(Protocol).Should().BeNull("payload has not arrived");

        connector.AppendReceived(frame.AsSpan(4));
        connector.TakeBufferedFrame(Protocol).Should().NotBeNull();
        connector.DroppedByteCount.Should().Be(0);
    }

    [Fact]
    public void DroppedByteCount_AdvancesByTheNumberOfBytesDropped()
    {
        using var connector = new BgapiConnector();
        connector.AppendReceived([0xFF, 0xFE, .. QueryBlockStatusResponseHeader(payloadLength: 0)]);

        connector.TakeBufferedFrame(Protocol).Should().NotBeNull();

        connector.DroppedByteCount.Should().Be(2);
    }

    // Resolving a header to a real definition is not on its own enough to accept it. A mid-payload
    // four-byte window can land on a real (device, class, index) triple, and its 11-bit length
    // field then claims up to 2047 bytes that continuing traffic supplies well before
    // StalledCandidateBudget could expire — swallowing whole real frames into a fabricated one.
    // The declared length must also be one that definition could actually produce.

    [Fact]
    public void IsKnownHeader_RealCommandWithImpossiblePayloadLength_IsRejected()
    {
        // query_block_status returns an errorcode and nothing else, so its payload is at most 2.
        var header = BgapiHeader.Parse(QueryBlockStatusResponseHeader(payloadLength: 2000));

        Protocol.IsKnownHeader(header).Should().BeFalse();
    }

    [Fact]
    public void IsKnownHeader_RealCommandAtItsLargestDeclaredSize_IsAccepted()
    {
        // errorcode + uint8 + uint16 + uint16 = 7 bytes. The bound is an upper bound only, so a
        // shorter payload — a truncated frame the decoder still reads as far as it goes — passes.
        Protocol.IsKnownHeader(BgapiHeader.Parse(GetStatusResponseHeader(payloadLength: 7)))
            .Should().BeTrue();
        Protocol.IsKnownHeader(BgapiHeader.Parse(GetStatusResponseHeader(payloadLength: 3)))
            .Should().BeTrue();
        Protocol.IsKnownHeader(BgapiHeader.Parse(GetStatusResponseHeader(payloadLength: 8)))
            .Should().BeFalse();
    }

    [Fact]
    public void TryExtractFrame_RealHeaderWithFabricatedLength_DoesNotSwallowTheFollowingFrame()
    {
        var fabricated = QueryBlockStatusResponseHeader(payloadLength: 2000);
        var good = GetStatusResponseHeader(payloadLength: 0);
        var buffer = fabricated.Concat(good).ToArray();

        var result = BgapiConnector.TryExtractFrame(buffer, Protocol, knownDeviceIds: null);

        result.Message.Should().NotBeNull("the following real frame must survive");
        result.Message!.Header.PayloadLength.Should().Be(0);
        result.DroppedBytes.Should().Be(fabricated.Length);
        result.Consumed.Should().Be(buffer.Length);
    }

    [Fact]
    public void AppendReceived_BeyondCapacity_ResyncsInsteadOfThrowing()
    {
        // An overflowing CopyTo throws before _rxCount advances, so nothing would ever drain the
        // accumulator and every later poll would throw identically — a permanently dead receive
        // path with no resync. Dropping the oldest bytes is recoverable; wedging is not.
        using var connector = new BgapiConnector();
        connector.AppendReceived(new byte[8000]);

        var overflowing = () => connector.AppendReceived(new byte[500]);

        overflowing.Should().NotThrow();
        connector.DroppedByteCount.Should().Be(308, "8000 + 500 exceeds the 8192-byte accumulator by 308");

        connector.AppendReceived(QueryBlockStatusResponseHeader(payloadLength: 0));
        connector.TakeBufferedFrame(Protocol).Should().NotBeNull("the receive path still works");
    }
}
