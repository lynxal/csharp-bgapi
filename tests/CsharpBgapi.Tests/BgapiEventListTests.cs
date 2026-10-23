using CsharpBgapi.Events;
using CsharpBgapi.Protocol;
using FluentAssertions;
using Xunit;

namespace CsharpBgapi.Tests;

public class BgapiEventListTests
{
    [Fact]
    public void CopyConstructor_CopiesTheEventsButNotTheFinalCount()
    {
        var header = new BgapiHeader((byte)(BgapiHeader.EventFlag | (5 << 3)), payloadLength: 0, classIndex: 0, commandIndex: 0);
        var message = new BgapiMessage(header, ReadOnlyMemory<byte>.Empty) { EventName = "btmesh_evt_vendor_model_receive" };
        var original = new BgapiEventList { message };
        original.FinalEventCount = 1;

        var copy = new BgapiEventList(original);

        copy.Should().ContainSingle().Which.Should().BeSameAs(message);
        copy.FinalEventCount.Should().Be(0, "a copy starts its own count");
    }
}
