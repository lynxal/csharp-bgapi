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
