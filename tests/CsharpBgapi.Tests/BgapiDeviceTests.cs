using CsharpBgapi.Protocol;
using Xunit;

namespace CsharpBgapi.Tests;

public class BgapiDeviceTests
{
    // A late response from a timed-out command A must not complete the TCS of the
    // next command B. ReaderLoop used to complete whatever TCS was pending with no
    // identity check, so B silently received A's status and parameters.

    private static BgapiMessage CreateResponse(byte deviceId, byte classIndex, byte commandIndex)
    {
        var header = BgapiHeader.CreateCommand(deviceId, classIndex, commandIndex, 0);
        return new BgapiMessage(header, ReadOnlyMemory<byte>.Empty);
    }

    [Fact]
    public void HandleResponseMessage_StaleResponse_DoesNotCompletePending()
    {
        using var device = new BgapiDevice();
        var tcs = device.InstallPendingCommand(BgapiHeader.CreateCommand(deviceId: 4, classIndex: 0x22, commandIndex: 0x09, payloadLength: 0));

        // Late response from the previous, timed-out command (different identity)
        device.HandleResponseMessage(CreateResponse(deviceId: 4, classIndex: 0x15, commandIndex: 0x01));

        Assert.False(tcs.Task.IsCompleted);
    }

    [Fact]
    public async Task HandleResponseMessage_MatchingResponse_CompletesPending()
    {
        using var device = new BgapiDevice();
        var tcs = device.InstallPendingCommand(BgapiHeader.CreateCommand(deviceId: 4, classIndex: 0x22, commandIndex: 0x09, payloadLength: 0));

        var response = CreateResponse(deviceId: 4, classIndex: 0x22, commandIndex: 0x09);
        device.HandleResponseMessage(response);

        Assert.True(tcs.Task.IsCompletedSuccessfully);
        Assert.Same(response, await tcs.Task);
    }

    [Fact]
    public void HandleResponseMessage_NoPendingCommand_DropsResponse()
    {
        using var device = new BgapiDevice();

        // Must not throw — response simply dropped
        device.HandleResponseMessage(CreateResponse(deviceId: 4, classIndex: 0x22, commandIndex: 0x09));
    }

    [Fact]
    public void HandleResponseMessage_AfterClear_DropsLateResponse()
    {
        using var device = new BgapiDevice();
        var tcs = device.InstallPendingCommand(BgapiHeader.CreateCommand(deviceId: 4, classIndex: 0x22, commandIndex: 0x09, payloadLength: 0));
        device.ClearPendingCommand();

        device.HandleResponseMessage(CreateResponse(deviceId: 4, classIndex: 0x22, commandIndex: 0x09));

        Assert.False(tcs.Task.IsCompleted);
    }
}
