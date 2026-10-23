using CsharpBgapi.Protocol;
using FluentAssertions;
using Xunit;

namespace CsharpBgapi.Tests;

public class BgapiProtocolEncodeTests
{
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

    /// <summary>Encodes the command and checks its header against the definitions; returns the payload.</summary>
    private static byte[] EncodePayload(string api, string className, string commandName, Dictionary<string, object>? parameters = null)
    {
        var frame = Protocol.EncodeCommand(api, className, commandName, parameters);

        var command = Definitions.GetCommand(api, className, commandName);
        var payload = frame[BgapiHeader.Size..];
        frame[..BgapiHeader.Size].Should().Equal(BgapiHeader.CreateCommand(
            Definitions.GetDeviceId(api), command.ClassIndex, command.Index, (ushort)payload.Length).ToBytes());
        return payload;
    }

    [Fact]
    public void EncodeCommand_Uint32_IsLittleEndian()
    {
        // bt.dfu.flash_set_address takes one uint32, "address".
        var payload = EncodePayload("bt", "dfu", "flash_set_address",
            new Dictionary<string, object> { { "address", 0x12345678u } });

        payload.Should().Equal(0x78, 0x56, 0x34, 0x12);
    }

    [Fact]
    public void EncodeCommand_Uint8Array_HasAOneByteLengthPrefix()
    {
        // bt.dfu.flash_upload takes one uint8array, "data".
        var payload = EncodePayload("bt", "dfu", "flash_upload",
            new Dictionary<string, object> { { "data", new byte[] { 0xAA, 0xBB, 0xCC } } });

        payload.Should().Equal(0x03, 0xAA, 0xBB, 0xCC);
    }

    [Fact]
    public void EncodeCommand_Uint16Array_HasATwoByteLittleEndianLengthPrefix()
    {
        // bt.gatt.read_multiple_characteristic_values takes a uint8 "connection", then a
        // uint16array "characteristic_list" (two handles, 0x0010 and 0x0020, as 4 LE bytes).
        var payload = EncodePayload("bt", "gatt", "read_multiple_characteristic_values",
            new Dictionary<string, object>
            {
                { "connection", (byte)1 },
                { "characteristic_list", new byte[] { 0x10, 0x00, 0x20, 0x00 } },
            });

        payload.Should().Equal(0x01, 0x04, 0x00, 0x10, 0x00, 0x20, 0x00);
    }

    [Fact]
    public void EncodeCommand_FixedLengthArray_IsZeroPaddedToItsLength()
    {
        // btmesh.node.set_uuid takes one uuid_128, "uuid": always 16 bytes, no length prefix.
        var payload = EncodePayload("btmesh", "node", "set_uuid",
            new Dictionary<string, object> { { "uuid", new byte[] { 0x01, 0x02, 0x03, 0x04 } } });

        payload.Should().Equal([0x01, 0x02, 0x03, 0x04, .. new byte[12]]);
    }

    [Fact]
    public void EncodeCommand_MissingParameter_IsEncodedAsZero()
    {
        // btmesh.node.get_seq_remaining takes one uint16, "elem_index".
        var payload = EncodePayload("btmesh", "node", "get_seq_remaining");

        payload.Should().Equal(0x00, 0x00);
    }
}
