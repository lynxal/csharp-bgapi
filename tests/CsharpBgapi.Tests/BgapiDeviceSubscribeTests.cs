using CsharpBgapi.Protocol;
using FluentAssertions;
using Xunit;

namespace CsharpBgapi.Tests;

public class BgapiDeviceSubscribeTests
{
    // The exact FullName dispatch looks up: device_name "btmesh", class vendor_model, event receive.
    private const string VendorReceive = "btmesh_evt_vendor_model_receive";

    private static BgapiMessage Event(string name)
    {
        var header = new BgapiHeader((byte)(BgapiHeader.EventFlag | (5 << 3)), payloadLength: 0, classIndex: 0, commandIndex: 0);
        return new BgapiMessage(header, ReadOnlyMemory<byte>.Empty) { EventName = name };
    }

    [Fact]
    public void DispatchToHandlers_SubscribedFullName_InvokesTheHandler()
    {
        using var device = new BgapiDevice();
        var received = new List<BgapiMessage>();
        device.Subscribe(VendorReceive, received.Add);
        var message = Event(VendorReceive);

        device.DispatchToHandlers(message);

        received.Should().ContainSingle().Which.Should().BeSameAs(message);
    }

    [Fact]
    public void DispatchToHandlers_NameWithoutTheDevicePrefix_IsNeverInvoked()
    {
        using var device = new BgapiDevice();
        var calls = 0;
        device.Subscribe("vendor_model_receive", _ => calls++);

        device.DispatchToHandlers(Event(VendorReceive));

        calls.Should().Be(0, "keys are exact FullNames — there is no prefix or wildcard matching");
    }

    [Fact]
    public void Subscribe_SameHandlerTwice_IsInvokedOnce()
    {
        using var device = new BgapiDevice();
        var calls = 0;
        Action<BgapiMessage> handler = _ => calls++;
        device.Subscribe(VendorReceive, handler);
        device.Subscribe(VendorReceive, handler);

        device.DispatchToHandlers(Event(VendorReceive));

        calls.Should().Be(1);
    }

    [Fact]
    public void Unsubscribe_StopsDelivery()
    {
        using var device = new BgapiDevice();
        var calls = 0;
        Action<BgapiMessage> handler = _ => calls++;
        device.Subscribe(VendorReceive, handler);

        device.Unsubscribe(VendorReceive, handler);
        device.DispatchToHandlers(Event(VendorReceive));

        calls.Should().Be(0);
    }

    [Fact]
    public void DispatchToHandlers_ThrowingHandler_DoesNotStopTheOthers()
    {
        using var device = new BgapiDevice();
        var calls = 0;
        device.Subscribe(VendorReceive, _ => throw new InvalidOperationException("handler bug"));
        device.Subscribe(VendorReceive, _ => calls++);

        var act = () => device.DispatchToHandlers(Event(VendorReceive));

        act.Should().NotThrow("a handler's exception must never reach the reader thread");
        calls.Should().Be(1);
    }

    // Dispatch runs on the reader thread for every event. It used to lock the handler list and copy
    // it into a new array per event, and its Debug lines built a params array (and boxed the handler
    // count) even with Debug off.
    [Fact]
    public void DispatchToHandlers_DebugOff_AllocatesNothing()
    {
        using var device = new BgapiDevice();
        device.Subscribe(VendorReceive, static _ => { });
        var subscribed = Event(VendorReceive);
        var unsubscribed = Event("btmesh_evt_vendor_model_send_complete");
        device.DispatchToHandlers(subscribed);
        device.DispatchToHandlers(unsubscribed);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            device.DispatchToHandlers(subscribed);
            device.DispatchToHandlers(unsubscribed);
        }

        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
    }
}
