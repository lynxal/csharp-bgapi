using SilabsBgapi.Protocol;

namespace SilabsBgapi.Events;

/// <summary>
/// Event categorization result.
/// </summary>
public enum EventCategory
{
    SelectFinal = 0,
    SelectContinue = 1,
    Ignore = 2
}

/// <summary>
/// Abstract base class for event selectors. Ports BGLibExt EventSelector pattern.
/// </summary>
public abstract class EventSelector
{
    public abstract EventCategory Categorize(BgapiMessage message, IReadOnlyList<BgapiMessage> selectedEvents);
    public abstract bool Stateless { get; }

    /// <summary>
    /// Factory method: auto-constructs an EventSelector from various input types.
    /// Matches Python build_event_selector behavior.
    /// - null -> PassThroughSelector
    /// - string -> NameParamSelector (single event name)
    /// - string[] -> NameParamSelector (multiple event names, same params)
    /// - Dictionary{string, Dictionary{string,object}?} -> NameParamSelector (per-name params)
    /// - EventSelector -> returned as-is
    /// - Func delegate -> CallableSelector
    /// </summary>
    public static EventSelector Build(
        object? rawSelector,
        Dictionary<string, object>? paramSubs = null,
        Dictionary<string, object>? eventParams = null)
    {
        if (rawSelector is null)
            return new PassThroughSelector();

        if (rawSelector is EventSelector sel)
            return sel;

        if (rawSelector is string name)
            return new NameParamSelector(name, eventParams, paramSubs: paramSubs);

        if (rawSelector is string[] names)
            return new NameParamSelector(names, eventParams, paramSubs: paramSubs);

        if (rawSelector is Dictionary<string, Dictionary<string, object>?> dict)
            return new NameParamSelector(dict, paramSubs: paramSubs);

        if (rawSelector is Func<BgapiMessage, IReadOnlyList<BgapiMessage>, EventCategory> func)
            return new CallableSelector(func);

        throw new ArgumentException($"Cannot construct EventSelector from {rawSelector.GetType().Name}");
    }
}

/// <summary>
/// Multi-value parameter constraint. Matches if the event param value equals any of the allowed values.
/// Ports Python EventParamValues to differentiate multi-option matching from iterable values.
/// </summary>
public sealed class EventParamValues : List<object>
{
    public EventParamValues(params object[] values) : base(values) { }
    public EventParamValues(IEnumerable<object> values) : base(values) { }
}

/// <summary>
/// Matches events by name and optional parameter constraints.
/// Supports:
/// - Single event name (string)
/// - Multiple event names (string[], each sharing same constraints)
/// - Per-name constraints (Dictionary{string, Dictionary{string,object}?})
/// - Parameter value matching via EventParamValues (multi-value OR)
/// - $param substitution: string values starting with "$" resolve from paramSubs dictionary
/// - #unique: compound key deduplication (string or string[])
/// - #final: false -> SelectContinue instead of SelectFinal
/// </summary>
public sealed class NameParamSelector : EventSelector
{
    private readonly Dictionary<string, Dictionary<string, object>> _eventNameDict = new();
    private bool _stateless = true;

    public override bool Stateless => _stateless;

    /// <summary>
    /// Single event name with optional parameter constraints.
    /// </summary>
    public NameParamSelector(
        string eventName,
        Dictionary<string, object>? paramConstraints = null,
        bool isFinal = true,
        Dictionary<string, object>? paramSubs = null)
    {
        var constraints = new Dictionary<string, object>();
        if (paramConstraints is not null)
        {
            foreach (var (key, value) in paramConstraints)
                constraints[key] = value;
        }
        if (!isFinal)
            constraints["#final"] = false;
        _eventNameDict[eventName] = constraints;
        NormalizeAndResolve(paramSubs);
    }

    /// <summary>
    /// Multiple event names sharing the same parameter constraints.
    /// </summary>
    public NameParamSelector(
        string[] eventNames,
        Dictionary<string, object>? paramConstraints = null,
        Dictionary<string, object>? paramSubs = null)
    {
        foreach (var name in eventNames)
        {
            var constraints = new Dictionary<string, object>();
            if (paramConstraints is not null)
            {
                foreach (var (key, value) in paramConstraints)
                    constraints[key] = value;
            }
            _eventNameDict[name] = constraints;
        }
        NormalizeAndResolve(paramSubs);
    }

    /// <summary>
    /// Per-name parameter constraints. Dictionary maps event name to its constraints (or null).
    /// </summary>
    public NameParamSelector(
        Dictionary<string, Dictionary<string, object>?> eventNameDict,
        Dictionary<string, object>? paramSubs = null)
    {
        foreach (var (name, constraints) in eventNameDict)
        {
            _eventNameDict[name] = constraints is not null
                ? new Dictionary<string, object>(constraints)
                : [];
        }
        NormalizeAndResolve(paramSubs);
    }

    private void NormalizeAndResolve(Dictionary<string, object>? paramSubs)
    {
        foreach (var (evtName, evtParams) in _eventNameDict)
        {
            // Normalize non-keyword param values to EventParamValues
            var keysToUpdate = new List<(string key, EventParamValues val)>();
            foreach (var (paramName, paramValue) in evtParams)
            {
                if (IsKeyword(paramName)) continue;
                if (paramValue is not EventParamValues)
                    keysToUpdate.Add((paramName, new EventParamValues(paramValue)));
            }
            foreach (var (key, val) in keysToUpdate)
                evtParams[key] = val;

            // Normalize #unique
            if (evtParams.TryGetValue("#unique", out var uniqueVal))
            {
                _stateless = false;
                if (uniqueVal is string s)
                    evtParams["#unique"] = new string[] { s };
                else if (uniqueVal is not string[])
                    throw new ArgumentException($"#unique must be string or string[], got {uniqueVal.GetType().Name}");
            }
        }

        // Resolve $param substitutions
        if (paramSubs is not null && paramSubs.Count > 0)
            ResolveParamSubs(paramSubs);
    }

    private void ResolveParamSubs(Dictionary<string, object> paramSubs)
    {
        foreach (var (_, evtParams) in _eventNameDict)
        {
            foreach (var (paramName, paramValue) in evtParams)
            {
                if (IsKeyword(paramName)) continue;
                if (paramValue is EventParamValues values)
                {
                    for (int i = 0; i < values.Count; i++)
                    {
                        if (values[i] is string str)
                        {
                            str = str.Trim();
                            if (str.StartsWith('$'))
                            {
                                var subKey = str[1..];
                                if (paramSubs.TryGetValue(subKey, out var subValue))
                                    values[i] = subValue;
                            }
                        }
                    }
                }
            }
        }
    }

    private static bool IsKeyword(string paramName) => paramName.StartsWith('#');

    public override EventCategory Categorize(BgapiMessage message, IReadOnlyList<BgapiMessage> selectedEvents)
    {
        if (message.EventName is null || !_eventNameDict.TryGetValue(message.EventName, out var expectedParams))
            return EventCategory.Ignore;

        // If no param constraints, it's a final event
        if (expectedParams.Count == 0)
            return EventCategory.SelectFinal;

        // Check all non-keyword parameter constraints
        bool allMatch = true;
        foreach (var (paramName, paramValue) in expectedParams)
        {
            if (IsKeyword(paramName)) continue;
            if (paramValue is not EventParamValues allowedValues) continue;

            if (message.Parameters is null ||
                !message.Parameters.TryGetValue(paramName, out var actualValue))
            {
                allMatch = false;
                break;
            }

            if (!allowedValues.Any(v => Equals(v, actualValue)))
            {
                allMatch = false;
                break;
            }
        }

        if (!allMatch)
            return EventCategory.Ignore;

        // Check #final
        bool isFinal = true;
        if (expectedParams.TryGetValue("#final", out var finalVal) && finalVal is false)
            isFinal = false;

        if (isFinal)
        {
            // Check #unique for deduplication
            if (expectedParams.TryGetValue("#unique", out var uniqueVal) && uniqueVal is string[] uniqueParams)
            {
                if (IsDuplicate(message, uniqueParams, selectedEvents))
                    return EventCategory.Ignore;
            }
            return EventCategory.SelectFinal;
        }

        return EventCategory.SelectContinue;
    }

    private static bool IsDuplicate(BgapiMessage message, string[] uniqueParams, IReadOnlyList<BgapiMessage> selectedEvents)
    {
        if (message.Parameters is null) return false;

        foreach (var existing in selectedEvents)
        {
            if (existing.EventName != message.EventName) continue;
            if (existing.Parameters is null) continue;

            bool allMatch = true;
            foreach (var paramName in uniqueParams)
            {
                if (!message.Parameters.TryGetValue(paramName, out var newVal) ||
                    !existing.Parameters.TryGetValue(paramName, out var existingVal) ||
                    !Equals(newVal, existingVal))
                {
                    allMatch = false;
                    break;
                }
            }
            if (allMatch) return true;
        }
        return false;
    }
}

/// <summary>
/// Wraps a delegate for event filtering.
/// </summary>
public sealed class CallableSelector : EventSelector
{
    private readonly Func<BgapiMessage, IReadOnlyList<BgapiMessage>, EventCategory> _categorize;

    public override bool Stateless => false;

    public CallableSelector(Func<BgapiMessage, IReadOnlyList<BgapiMessage>, EventCategory> categorize)
    {
        _categorize = categorize;
    }

    public override EventCategory Categorize(BgapiMessage message, IReadOnlyList<BgapiMessage> selectedEvents) =>
        _categorize(message, selectedEvents);
}

/// <summary>
/// Accepts all events.
/// </summary>
public sealed class PassThroughSelector : EventSelector
{
    public override bool Stateless => true;

    public override EventCategory Categorize(BgapiMessage message, IReadOnlyList<BgapiMessage> selectedEvents) =>
        EventCategory.SelectContinue;
}

/// <summary>
/// Composes multiple child selectors. Returns the first non-Ignore result.
/// </summary>
public sealed class CompositeSelector : EventSelector
{
    private readonly List<EventSelector> _children;

    public override bool Stateless => _children.All(c => c.Stateless);

    public CompositeSelector(params EventSelector[] children)
    {
        _children = [.. children];
    }

    public CompositeSelector(IEnumerable<EventSelector> children)
    {
        _children = [.. children];
    }

    public override EventCategory Categorize(BgapiMessage message, IReadOnlyList<BgapiMessage> selectedEvents)
    {
        foreach (var child in _children)
        {
            var result = child.Categorize(message, selectedEvents);
            if (result != EventCategory.Ignore)
                return result;
        }
        return EventCategory.Ignore;
    }
}
