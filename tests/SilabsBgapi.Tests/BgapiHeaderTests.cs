using FluentAssertions;
using SilabsBgapi.Protocol;
using Xunit;

namespace SilabsBgapi.Tests;

public class BgapiHeaderTests
{
    [Fact]
    public void Parse_ShouldDecodeHeaderCorrectly()
    {
        // Command response: device=4 (bt), class=1, index=0, payload=2 bytes
        byte[] data = [0x20, 0x02, 0x01, 0x00];
        var header = BgapiHeader.Parse(data);

        header.ClassIndex.Should().Be(1);
        header.CommandIndex.Should().Be(0);
        header.PayloadLength.Should().Be(2);
        header.IsEvent.Should().BeFalse();
        header.IsResponse.Should().BeTrue();
    }

    [Fact]
    public void Parse_Event_ShouldSetEventFlag()
    {
        // Event: bit 7 set
        byte[] data = [0xA0, 0x04, 0x02, 0x01];
        var header = BgapiHeader.Parse(data);

        header.IsEvent.Should().BeTrue();
        header.IsResponse.Should().BeFalse();
        header.PayloadLength.Should().Be(4);
        header.ClassIndex.Should().Be(2);
        header.CommandIndex.Should().Be(1);
    }

    [Fact]
    public void CreateCommand_ShouldProduceValidHeader()
    {
        var header = BgapiHeader.CreateCommand(deviceId: 4, classIndex: 1, commandIndex: 0, payloadLength: 5);

        header.ClassIndex.Should().Be(1);
        header.CommandIndex.Should().Be(0);
        header.PayloadLength.Should().Be(5);
    }

    [Fact]
    public void RoundTrip_ShouldPreserveValues()
    {
        var original = BgapiHeader.CreateCommand(4, 3, 7, 100);
        var bytes = original.ToBytes();
        var parsed = BgapiHeader.Parse(bytes);

        parsed.ClassIndex.Should().Be(original.ClassIndex);
        parsed.CommandIndex.Should().Be(original.CommandIndex);
        parsed.PayloadLength.Should().Be(original.PayloadLength);
    }

    [Fact]
    public void Parse_TooShort_ShouldThrow()
    {
        byte[] data = [0x20, 0x02];
        var act = () => BgapiHeader.Parse(data);
        act.Should().Throw<ArgumentException>();
    }
}
