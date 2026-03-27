using FluentAssertions;
using CsharpBgapi.Events;
using CsharpBgapi.Protocol;
using Xunit;

namespace CsharpBgapi.Tests;

public class EventSelectorTests
{
    private static BgapiMessage CreateMessage(string eventName, Dictionary<string, object>? parameters = null)
    {
        var header = new BgapiHeader(0xA0, 0, 0, 0);
        return new BgapiMessage(header, ReadOnlyMemory<byte>.Empty)
        {
            EventName = eventName,
            Parameters = parameters
        };
    }

    [Fact]
    public void NameParamSelector_ShouldMatchByName()
    {
        var selector = new NameParamSelector("bt_evt_system_boot");
        var msg = CreateMessage("bt_evt_system_boot");

        selector.Categorize(msg, []).Should().Be(EventCategory.SelectFinal);
    }

    [Fact]
    public void NameParamSelector_ShouldIgnoreNonMatching()
    {
        var selector = new NameParamSelector("bt_evt_system_boot");
        var msg = CreateMessage("bt_evt_connection_opened");

        selector.Categorize(msg, []).Should().Be(EventCategory.Ignore);
    }

    [Fact]
    public void NameParamSelector_WithParamConstraints_ShouldFilterByParam()
    {
        var selector = new NameParamSelector("bt_evt_mesh_event",
            new Dictionary<string, object> { { "address", (ushort)256 } });

        var match = CreateMessage("bt_evt_mesh_event",
            new Dictionary<string, object> { { "address", (ushort)256 } });
        var noMatch = CreateMessage("bt_evt_mesh_event",
            new Dictionary<string, object> { { "address", (ushort)512 } });

        selector.Categorize(match, []).Should().Be(EventCategory.SelectFinal);
        selector.Categorize(noMatch, []).Should().Be(EventCategory.Ignore);
    }

    [Fact]
    public void PassThroughSelector_ShouldAcceptAll()
    {
        var selector = new PassThroughSelector();
        var msg = CreateMessage("any_event");

        selector.Categorize(msg, []).Should().Be(EventCategory.SelectContinue);
    }

    [Fact]
    public void CompositeSelector_ShouldReturnFirstNonIgnore()
    {
        var selector = new CompositeSelector(
            new NameParamSelector("event_a"),
            new NameParamSelector("event_b"));

        var msgA = CreateMessage("event_a");
        var msgB = CreateMessage("event_b");
        var msgC = CreateMessage("event_c");

        selector.Categorize(msgA, []).Should().Be(EventCategory.SelectFinal);
        selector.Categorize(msgB, []).Should().Be(EventCategory.SelectFinal);
        selector.Categorize(msgC, []).Should().Be(EventCategory.Ignore);
    }

    [Fact]
    public void CallableSelector_ShouldDelegateToFunction()
    {
        var selector = new CallableSelector((msg, _) =>
            msg.EventName == "test" ? EventCategory.SelectFinal : EventCategory.Ignore);

        var match = CreateMessage("test");
        var noMatch = CreateMessage("other");

        selector.Categorize(match, []).Should().Be(EventCategory.SelectFinal);
        selector.Categorize(noMatch, []).Should().Be(EventCategory.Ignore);
    }
}
