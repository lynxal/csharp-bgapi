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
    /// Numeric values are widened/converted: decoded parameters carry their exact
    /// xapi width (uint16 → ushort), so a strict type check would make
    /// GetParameter&lt;int&gt;("address") silently return 0 for a ushort-decoded value.
    /// </summary>
    public T? GetParameter<T>(string name)
    {
        if (Parameters is null || !Parameters.TryGetValue(name, out var value))
            return default;

        if (value is T typed)
            return typed;

        if (value is IConvertible && !typeof(T).IsArray)
        {
            try
            {
                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
            {
                return default;
            }
        }

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
