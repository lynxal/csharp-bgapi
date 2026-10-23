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

    [Fact]
    public void NameParamSelector_EventParamValues_MatchesAnyListedValue()
    {
        var selector = new NameParamSelector(VendorReceive,
            new Dictionary<string, object> { { "source_address", new EventParamValues((ushort)256, (ushort)512) } });

        selector.Categorize(FromNode(256), []).Should().Be(EventCategory.SelectFinal);
        selector.Categorize(FromNode(512), []).Should().Be(EventCategory.SelectFinal);
        selector.Categorize(FromNode(768), []).Should().Be(EventCategory.Ignore);
    }

    // A "$name" constraint stands for a value the caller only learns later — in RetryUntilAsync,
    // from the command response. NameParamSelector resolved placeholders once, at construction, by
    // overwriting them in place, and WaitEvents never read the paramSubs it was handed, so a
    // selector built before the response existed kept the literal "$address" string and never
    // matched. Resolving also wrote the substituted value into an EventParamValues the caller had
    // passed in.

    private const string VendorReceive = "btmesh_evt_vendor_model_receive";

    private static BgapiMessage FromNode(ushort address) =>
        CreateMessage(VendorReceive, new Dictionary<string, object> { { "source_address", address } });

    private static NameParamSelector FromPlaceholder() =>
        new(VendorReceive, new Dictionary<string, object> { { "source_address", "$address" } });

    private static Dictionary<string, object> Address(ushort address) => new() { { "address", address } };

    [Fact]
    public void WithParamSubs_ResolvesDollarPlaceholder()
    {
        var selector = FromPlaceholder();

        selector.Categorize(FromNode(256), []).Should().Be(EventCategory.Ignore,
            "an unresolved placeholder matches no real value");
        selector.WithParamSubs(Address(256)).Categorize(FromNode(256), []).Should().Be(EventCategory.SelectFinal);
    }

    [Fact]
    public void WithParamSubs_SameSelectorResolvesAgainWithNewValues()
    {
        var selector = FromPlaceholder();

        var first = selector.WithParamSubs(Address(256));
        var second = selector.WithParamSubs(Address(512));

        first.Categorize(FromNode(256), []).Should().Be(EventCategory.SelectFinal);
        second.Categorize(FromNode(512), []).Should().Be(EventCategory.SelectFinal);
        second.Categorize(FromNode(256), []).Should().Be(EventCategory.Ignore,
            "the second response's value replaces the first, it does not join it");
    }

    [Fact]
    public void WithParamSubs_KeepsConstructionTimeSubstitutions()
    {
        var selector = new NameParamSelector(VendorReceive,
            new Dictionary<string, object> { { "source_address", "$address" }, { "model_id", "$model" } },
            paramSubs: new Dictionary<string, object> { { "model", (ushort)0x1234 } });
        var message = CreateMessage(VendorReceive,
            new Dictionary<string, object> { { "source_address", (ushort)256 }, { "model_id", (ushort)0x1234 } });

        selector.WithParamSubs(Address(256)).Categorize(message, []).Should().Be(EventCategory.SelectFinal);
    }

    [Fact]
    public void Constructor_ParamSubs_DoNotWriteIntoTheCallersValues()
    {
        var allowed = new EventParamValues("$address");

        _ = new NameParamSelector(VendorReceive,
            new Dictionary<string, object> { { "source_address", allowed } },
            paramSubs: Address(256));

        allowed.Should().Equal("$address");
    }

    [Fact]
    public void CompositeSelector_WithParamSubs_ResolvesEveryChild()
    {
        const string otherEvent = "btmesh_evt_vendor_model_send_complete";
        var selector = new CompositeSelector(
            FromPlaceholder(),
            new NameParamSelector(otherEvent, new Dictionary<string, object> { { "source_address", "$address" } }));
        var other = CreateMessage(otherEvent, new Dictionary<string, object> { { "source_address", (ushort)256 } });

        var resolved = selector.WithParamSubs(Address(256));

        resolved.Categorize(FromNode(256), []).Should().Be(EventCategory.SelectFinal);
        resolved.Categorize(other, []).Should().Be(EventCategory.SelectFinal);
    }

    // RetryUntilAsync passes the whole command response as per-call substitutions. They used to
    // override the ones the caller fixed at construction, so a response field that shared a name
    // with one of those silently replaced the caller's value.

    [Fact]
    public void WithParamSubs_ConstructionTimeSubstitutionsWinOverPerCallOnes()
    {
        var selector = new NameParamSelector(VendorReceive,
            new Dictionary<string, object> { { "source_address", "$address" } },
            paramSubs: Address(256));

        var resolved = selector.WithParamSubs(Address(512));

        resolved.Categorize(FromNode(256), []).Should().Be(EventCategory.SelectFinal);
        resolved.Categorize(FromNode(512), []).Should().Be(EventCategory.Ignore);
    }

    // The substitutions were copied with the default comparer, so a caller's case-insensitive
    // dictionary resolved "$address" at construction and no longer did when re-resolved.

    [Fact]
    public void WithParamSubs_KeepsTheCallersKeyComparer()
    {
        var selector = new NameParamSelector(VendorReceive,
            new Dictionary<string, object> { { "source_address", "$address" } },
            paramSubs: new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { { "Address", (ushort)256 } });

        var resolved = selector.WithParamSubs(new Dictionary<string, object> { { "unrelated", 1 } });

        resolved.Categorize(FromNode(256), []).Should().Be(EventCategory.SelectFinal);
    }

    [Fact]
    public void WithParamSubs_NoPlaceholders_ReturnsTheSameSelector()
    {
        var selector = new NameParamSelector(VendorReceive,
            new Dictionary<string, object> { { "source_address", (ushort)256 } });

        selector.WithParamSubs(Address(512)).Should().BeSameAs(selector);
    }

    // Build accepted paramSubs and returned an already-built selector unchanged, so its "$name"
    // constraints kept their literal placeholder and never matched.

    [Fact]
    public void Build_PrebuiltSelector_AppliesParamSubs()
    {
        var selector = EventSelector.Build(FromPlaceholder(), Address(256));

        selector.Categorize(FromNode(256), []).Should().Be(EventCategory.SelectFinal);
    }

    // Constraints were compared with boxed Equals, which needs the same CLR type and compares
    // arrays by reference: an int literal never matched the ushort an event decodes to, and a
    // bd_addr or uuid (byte[]) never matched at all, nor deduplicated under #unique.

    private static BgapiMessage WithAddress(object address) =>
        CreateMessage(VendorReceive, new Dictionary<string, object> { { "address", address } });

    [Fact]
    public void NameParamSelector_IntegerConstraint_MatchesAnyIntegerTypeOfTheSameValue()
    {
        var selector = new NameParamSelector(VendorReceive, new Dictionary<string, object> { { "address", 256 } });

        selector.Categorize(WithAddress((ushort)256), []).Should().Be(EventCategory.SelectFinal);
        selector.Categorize(WithAddress((ushort)257), []).Should().Be(EventCategory.Ignore);
    }

    [Fact]
    public void NameParamSelector_SignedAndUnsignedOfDifferentValue_DoNotMatch()
    {
        var selector = new NameParamSelector(VendorReceive, new Dictionary<string, object> { { "address", -1 } });

        selector.Categorize(WithAddress(ushort.MaxValue), []).Should().Be(EventCategory.Ignore);
    }

    [Fact]
    public void NameParamSelector_ByteArrayConstraint_MatchesByContent()
    {
        var selector = new NameParamSelector(VendorReceive,
            new Dictionary<string, object> { { "address", new byte[] { 1, 2, 3, 4, 5, 6 } } });

        selector.Categorize(WithAddress(new byte[] { 1, 2, 3, 4, 5, 6 }), []).Should().Be(EventCategory.SelectFinal);
        selector.Categorize(WithAddress(new byte[] { 1, 2, 3, 4, 5, 7 }), []).Should().Be(EventCategory.Ignore);
    }

    [Fact]
    public void NameParamSelector_UniqueByteArrayParam_DeduplicatesByContent()
    {
        var selector = new NameParamSelector(VendorReceive,
            new Dictionary<string, object> { { "#unique", "address" } });
        var first = WithAddress(new byte[] { 1, 2, 3, 4, 5, 6 });

        selector.Categorize(WithAddress(new byte[] { 1, 2, 3, 4, 5, 6 }), [first]).Should().Be(EventCategory.Ignore);
    }
}
