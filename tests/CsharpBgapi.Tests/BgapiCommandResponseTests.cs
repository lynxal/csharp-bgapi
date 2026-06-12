using Xunit;

namespace CsharpBgapi.Tests;

public class BgapiCommandResponseTests
{
    // Decoded parameters carry their exact xapi width (uint16 -> ushort).
    // GetParameter<T> used a strict 'is T' check, so GetParameter<int>("address")
    // on a ushort-decoded value silently returned default(int) = 0.

    private static BgapiCommandResponse CreateResponse(params (string Name, object Value)[] parameters) => new()
    {
        Status = SlStatus.OK,
        Parameters = parameters.ToDictionary(p => p.Name, p => p.Value)
    };

    [Fact]
    public void GetParameter_WidensUshortToInt()
    {
        var response = CreateResponse(("address", (ushort)7));

        Assert.Equal(7, response.GetParameter<int>("address"));
    }

    [Fact]
    public void GetParameter_WidensByteToUshort()
    {
        var response = CreateResponse(("count", (byte)3));

        Assert.Equal((ushort)3, response.GetParameter<ushort>("count"));
    }

    [Fact]
    public void GetParameter_ExactTypeMatch_StillWorks()
    {
        var response = CreateResponse(("address", (ushort)7));

        Assert.Equal((ushort)7, response.GetParameter<ushort>("address"));
    }

    [Fact]
    public void GetParameter_ByteArray_ReturnsArray()
    {
        var payload = new byte[] { 1, 2, 3 };
        var response = CreateResponse(("data", payload));

        Assert.Same(payload, response.GetParameter<byte[]>("data"));
    }

    [Fact]
    public void GetParameter_IncompatibleArrayType_ReturnsDefault()
    {
        // uint8array decodes to byte[]; asking for ushort[] cannot be widened
        var response = CreateResponse(("appkeys", new byte[] { 1, 0 }));

        Assert.Null(response.GetParameter<ushort[]>("appkeys"));
    }

    [Fact]
    public void GetParameter_MissingParameter_ReturnsDefault()
    {
        var response = CreateResponse(("address", (ushort)7));

        Assert.Equal(0, response.GetParameter<int>("missing"));
    }

    [Fact]
    public void GetParameter_OverflowingNarrowingConversion_ReturnsDefault()
    {
        var response = CreateResponse(("value", (uint)70000));

        Assert.Equal((ushort)0, response.GetParameter<ushort>("value"));
    }
}
