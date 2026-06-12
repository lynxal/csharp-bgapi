using CsharpBgapi.Protocol;
using Xunit;

namespace CsharpBgapi.Tests;

public class BgapiProtocolDecodeTests
{
    // A truncated payload (xapi/firmware mismatch, corrupted frame with a valid
    // header) must not throw out of DecodeParameters — the exception dropped the
    // whole message in ReaderLoop and stalled the pending command until timeout.

    [Fact]
    public void DecodeParameters_TruncatedFixedWidthParam_StopsWithoutThrowing()
    {
        // Definition expects uuid_128 (16 bytes), payload only has 14
        var defs = new[] { new XapiParameter("uuid", "uuid_128", "uuid_128", 16) };
        var data = new byte[14];

        var result = BgapiProtocol.DecodeParameters(defs, data);

        Assert.Empty(result);
    }

    [Fact]
    public void DecodeParameters_TruncatedArrayPayload_StopsWithoutThrowing()
    {
        // Length prefix claims 10 bytes but only 4 follow
        var defs = new[] { new XapiParameter("data", "uint8array", "uint8array", 0) };
        var data = new byte[] { 10, 1, 2, 3, 4 };

        var result = BgapiProtocol.DecodeParameters(defs, data);

        Assert.Empty(result);
    }

    [Fact]
    public void DecodeParameters_TruncationMidList_KeepsEarlierParams()
    {
        // First param decodes, second is truncated — keep the first, stop cleanly
        var defs = new[]
        {
            new XapiParameter("result", "errorcode", "errorcode", 2),
            new XapiParameter("address", "uint64", "uint64", 8),
        };
        var data = new byte[] { 0x00, 0x00, 0x01, 0x02, 0x03 }; // 2 + only 3 of 8

        var result = BgapiProtocol.DecodeParameters(defs, data);

        Assert.Single(result);
        Assert.Equal((ushort)0, result["result"]);
    }

    [Fact]
    public void DecodeParameters_CompletePayload_DecodesAllParams()
    {
        var defs = new[]
        {
            new XapiParameter("result", "errorcode", "errorcode", 2),
            new XapiParameter("address", "uint16", "uint16", 2),
        };
        var data = new byte[] { 0x00, 0x00, 0x07, 0x00 };

        var result = BgapiProtocol.DecodeParameters(defs, data);

        Assert.Equal(2, result.Count);
        Assert.Equal((ushort)0, result["result"]);
        Assert.Equal((ushort)7, result["address"]);
    }
}
