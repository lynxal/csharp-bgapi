using CsharpBgapi.Protocol;

namespace CsharpBgapi.Events;

/// <summary>
/// Event list with final event count tracking. Ports Python EventList.
/// Used for accumulating events across retry iterations.
/// </summary>
public sealed class BgapiEventList : List<BgapiMessage>
{
    public int FinalEventCount { get; set; }

    public BgapiEventList() { }

    public BgapiEventList(IEnumerable<BgapiMessage> collection) : base(collection) { }
}
