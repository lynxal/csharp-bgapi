using CsharpBgapi.Protocol;
using FluentAssertions;
using Microsoft.Extensions.Logging;
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

    // Framing desync is detected and reported by the connector, which never emits a frame the
    // definitions cannot name. What reaches here is only ever a genuine late reply from a
    // previous, timed-out command, and it must be dropped without completing the pending TCS.

    [Fact]
    public void HandleResponseMessage_NameableMismatch_LogsStaleResponseAndDropsIt()
    {
        var logger = new RecordingLogger();
        using var device = new BgapiDevice(logger);
        var tcs = device.InstallPendingCommand(BgapiHeader.CreateCommand(deviceId: 5, classIndex: 40, commandIndex: 5, payloadLength: 0));

        // A real, nameable response from a different command — a genuine late reply.
        var late = new BgapiMessage(BgapiHeader.CreateCommand(deviceId: 4, classIndex: 0x22, commandIndex: 0x09, payloadLength: 0), ReadOnlyMemory<byte>.Empty)
        {
            EventName = "bt_cmd_something_response"
        };
        device.HandleResponseMessage(late);

        tcs.Task.IsCompleted.Should().BeFalse();
        logger.Warnings.Should().ContainSingle().Which.Should().Contain("stale response");
    }

    // GetEventId used to initialise clsIdx/evtIdx to 0 and assign them only on a name match, so
    // an unknown class or event name still composed and returned an id — the one addressing
    // class 0, event 0. AddEventFilter/RemoveEventFilter then installed or removed a filter on a
    // different event than the caller named, and the NCP answered OK, giving the caller positive
    // confirmation of the wrong action.

    [Fact]
    public void GetEventId_ResolvingNames_ComposesId()
    {
        using var device = new BgapiDevice();
        device.LoadDefaultXapis();

        // btmesh: device_id 5, class vendor_model index 25, event receive index 0.
        var eventId = device.GetEventId("btmesh", "vendor_model", "receive");

        eventId.Should().Be((0u << 24) | (25u << 16) | 0x80 | (5u << 3));
    }

    [Fact]
    public void GetEventId_UnknownClassName_Throws()
    {
        using var device = new BgapiDevice();
        device.LoadDefaultXapis();

        var act = () => device.GetEventId("btmesh", "mesh", "receive");

        act.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void GetEventId_UnknownEventName_Throws()
    {
        using var device = new BgapiDevice();
        device.LoadDefaultXapis();

        var act = () => device.GetEventId("btmesh", "vendor_model", "vendor_model_receive");

        act.Should().Throw<KeyNotFoundException>();
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }
}
