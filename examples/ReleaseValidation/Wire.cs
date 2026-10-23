using System.Buffers.Binary;
using CsharpBgapi.Protocol;

namespace CsharpBgapi.Validation;

internal static class Wire
{
    public static XapiDefinitions LoadDefinitions()
    {
        var definitions = new XapiDefinitions();
        foreach (var name in EmbeddedXapiResources.AllResourceNames)
        {
            using var stream = EmbeddedXapiResources.OpenResource(name);
            definitions.LoadFromStream(stream);
        }
        return definitions;
    }

    public static byte[] Response(XapiDefinitions definitions, string command = "halt", ushort status = 0) =>
        Response(definitions.GetCommand("bt", "system", command), definitions.GetDeviceId("bt"), status);

    /// <summary>The response to a command just written, carrying that command's header identity.</summary>
    public static byte[] ResponseTo(XapiDefinitions definitions, byte[] written, ushort status = 0)
    {
        var header = BgapiHeader.Parse(written);
        return Response(definitions.FindCommand(header.DeviceId, header.ClassIndex, header.CommandIndex)!,
            header.DeviceId, status);
    }

    // Sized from the declared returns, which are fixed-size for every bt.system command used here
    // (set_tx_power answers 6 bytes, not 2). Each starts with an LE errorcode; the rest stay zero.
    private static byte[] Response(CommandDefinition definition, byte deviceId, ushort status)
    {
        var payload = new byte[BgapiProtocol.MaxPayloadLength(definition.Returns)];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, status);
        return [.. BgapiHeader.CreateCommand(deviceId, definition.ClassIndex,
            definition.Index, (ushort)payload.Length).ToBytes(), .. payload];
    }

    public static byte[] Event(XapiDefinitions definitions, int sequence, int size = 16)
    {
        var definition = definitions.GetClass("bt", "user").Events.Single(e => e.Name == "message_to_host");
        var data = new byte[size];
        BinaryPrimitives.WriteInt32LittleEndian(data, sequence);
        // user.message_to_host has one uint8array, whose first byte is its length.
        byte[] payload = [(byte)data.Length, .. data];
        var header = new BgapiHeader((byte)(0x80 | definitions.GetDeviceId("bt") << 3),
            (ushort)payload.Length, definition.ClassIndex, definition.Index);
        return [.. header.ToBytes(), .. payload];
    }

    public static int Sequence(BgapiMessage message) =>
        BinaryPrimitives.ReadInt32LittleEndian(message.Parameters!.Values.OfType<byte[]>().Single());
}
