using CsharpBgapi.Protocol;

namespace CsharpBgapi.Events;

/// <summary>
/// Event list with final event count tracking. Ports Python EventList.
/// Used for accumulating events across retry iterations.
/// </summary>
public sealed class BgapiEventList : List<BgapiMessage>
{
    // The events counted in FinalEventCount, so one dropped before a retry gives back its count.
    private readonly HashSet<BgapiMessage> _counted = [];

    public int FinalEventCount { get; set; }

    public BgapiEventList() { }

    public BgapiEventList(IEnumerable<BgapiMessage> collection) : base(collection) { }

    internal void CountFinal(BgapiMessage message)
    {
        _counted.Add(message);
        FinalEventCount++;
    }

    /// <summary>Removes the events <paramref name="match"/> selects, taking back the count of each one counted.</summary>
    internal void Drop(Predicate<BgapiMessage> match)
    {
        foreach (var message in FindAll(match))
        {
            Remove(message);
            if (_counted.Remove(message))
                FinalEventCount--;
        }
    }
}
