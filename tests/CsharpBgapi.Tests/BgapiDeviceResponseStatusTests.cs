using CsharpBgapi.Protocol;
using FluentAssertions;
using Xunit;

namespace CsharpBgapi.Tests;

public class BgapiDeviceResponseStatusTests
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

    /// <summary>
    /// The response frame to <paramref name="api"/>.<paramref name="className"/>.<paramref name="commandName"/>
    /// with <paramref name="payload"/>, decoded the way the reader thread decodes it. Indices come
    /// from the definitions, never literals.
    /// </summary>
    private static (CommandDefinition Command, BgapiMessage Response) Respond(
        string api, string className, string commandName, params byte[] payload)
    {
        var command = Definitions.GetCommand(api, className, commandName);
        var header = BgapiHeader.CreateCommand(
            Definitions.GetDeviceId(api), command.ClassIndex, command.Index, (ushort)payload.Length);
        return (command, Protocol.DecodeMessage([.. header.ToBytes(), .. payload]));
    }

    [Fact]
    public void ResponseStatus_NonZeroErrorcode_MapsToItsStatus()
    {
        // bt.system.hello returns one errorcode, "result"; 0x0021 little-endian is InvalidParameter.
        var (command, response) = Respond("bt", "system", "hello", 0x21, 0x00);

        BgapiDevice.ResponseStatus(command, response).Should().Be(SlStatus.InvalidParameter);
    }

    [Fact]
    public void ResponseStatus_ZeroErrorcode_IsOk()
    {
        var (command, response) = Respond("bt", "system", "hello", 0x00, 0x00);

        BgapiDevice.ResponseStatus(command, response).Should().Be(SlStatus.OK);
    }

    [Fact]
    public void ResponseStatus_ErrorcodeNotInTheEnum_IsUnknownValue()
    {
        // 0xBEEF little-endian: no SlStatus member carries it.
        var (command, response) = Respond("bt", "system", "hello", 0xEF, 0xBE);

        BgapiDevice.ResponseStatus(command, response).Should().Be(SlStatus.UnknownValue);
    }

    [Fact]
    public void ResponseStatus_PayloadEndsBeforeTheErrorcode_IsFail()
    {
        // btmesh mbt_client.get_status returns errorcode + uint8 + uint16 + uint16; the framing
        // check accepts any length up to that, so an empty payload decodes with no "result".
        // Reported as OK, a truncated or resynced frame let the caller carry on as if the NCP had
        // accepted the command.
        var (command, response) = Respond("btmesh", "mbt_client", "get_status");

        BgapiDevice.ResponseStatus(command, response).Should().Be(SlStatus.Fail,
            "a response that never reached its errorcode cannot report success");
    }
}
