using CsharpBgapi.Protocol;

namespace CsharpBgapi;

/// <summary>
/// Full response from a BGAPI command, including status code, all parameters, and raw message.
/// </summary>
public sealed record BgapiCommandResponse
{
    public SlStatus Status { get; init; }
    public Dictionary<string, object>? Parameters { get; init; }
    public BgapiMessage? RawMessage { get; init; }

    /// <summary>
    /// Gets a typed parameter value from the response. Returns default if not found.
    /// </summary>
    public T? GetParameter<T>(string name)
    {
        if (Parameters is not null && Parameters.TryGetValue(name, out var value) && value is T typed)
            return typed;
        return default;
    }

    /// <summary>
    /// Builds a parameter substitution dictionary from response parameters.
    /// Used by retry_until to pass command output values into event selectors.
    /// </summary>
    public Dictionary<string, object> BuildParamSubs()
    {
        return Parameters is not null
            ? new Dictionary<string, object>(Parameters)
            : [];
    }
}
