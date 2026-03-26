namespace SilabsBgapi.Protocol;

/// <summary>
/// A decoded BGAPI message (command response or event) with its header and payload.
/// </summary>
public sealed class BgapiMessage
{
    public BgapiHeader Header { get; }
    public ReadOnlyMemory<byte> Payload { get; }
    public string? EventName { get; init; }
    public Dictionary<string, object>? Parameters { get; init; }

    public bool IsEvent => Header.IsEvent;
    public bool IsResponse => Header.IsResponse;
    public byte DeviceId => Header.DeviceId;
    public byte ClassIndex => Header.ClassIndex;
    public byte CommandIndex => Header.CommandIndex;

    public BgapiMessage(BgapiHeader header, ReadOnlyMemory<byte> payload)
    {
        Header = header;
        Payload = payload;
    }

    public T GetParameter<T>(string name)
    {
        if (Parameters is null || !Parameters.TryGetValue(name, out var value))
            throw new KeyNotFoundException($"Parameter '{name}' not found in message");
        return (T)value;
    }

    public bool TryGetParameter<T>(string name, out T? result)
    {
        if (Parameters is not null && Parameters.TryGetValue(name, out var value) && value is T typed)
        {
            result = typed;
            return true;
        }
        result = default;
        return false;
    }

    public override string ToString() =>
        $"BgapiMessage[{(IsEvent ? "Event" : "Response")} dev={DeviceId} cls={ClassIndex} idx={CommandIndex} name={EventName ?? "?"} len={Header.PayloadLength}]";
}
