using System.Xml.Linq;

namespace CsharpBgapi.Protocol;

/// <summary>
/// Parses and holds XAPI XML definitions for BGAPI command/event structures.
/// </summary>
public sealed class XapiDefinitions
{
    private readonly Dictionary<string, ApiDefinition> _apis = new();

    // (deviceId, classIndex, index) -> the definition plus the largest payload it can describe.
    // Framing asks this once per candidate byte while resyncing, so the chained linear scans this
    // replaces sat on the reader thread's hot path.
    private readonly Dictionary<int, (CommandDefinition Def, int MaxPayload)> _commandsByHeader = new();
    private readonly Dictionary<int, (EventDefinition Def, int MaxPayload)> _eventsByHeader = new();

    /// <summary>
    /// Returns true if at least one XAPI definition has been loaded.
    /// </summary>
    public bool HasDefinitions => _apis.Count > 0;

    /// <summary>
    /// Returns the names of all loaded APIs (e.g., "bt", "btmesh").
    /// </summary>
    public IReadOnlyCollection<string> LoadedApiNames => _apis.Keys;

    public ApiDefinition LoadFromFile(string path)
    {
        var doc = XDocument.Load(path);
        var root = doc.Root ?? throw new InvalidOperationException("Empty XAPI file");
        var api = ParseApi(root);
        EnsureDeviceIdUnclaimed(api);
        _apis[api.Name] = api;
        Reindex();
        return api;
    }

    public ApiDefinition LoadFromStream(Stream stream)
    {
        var doc = XDocument.Load(stream);
        var root = doc.Root ?? throw new InvalidOperationException("Empty XAPI file");
        var api = ParseApi(root);
        EnsureDeviceIdUnclaimed(api);
        _apis[api.Name] = api;
        Reindex();
        return api;
    }

    // APIs are stored by name but every inbound frame resolves by device id, and Reindex assigns
    // into _commandsByHeader/_eventsByHeader by plain indexer. So a second API claiming a loaded
    // device id used to overwrite the first one's entries silently -- last loaded wins. That
    // decodes frames under the wrong definition, and MaxPayloadFor (which IsKnownHeader and the
    // resync plausibility check read) then answers from the wrong definition too. A definition set
    // that parses but misroutes is a malformed static contract, so it fails loud at load like the
    // ParseApi checks above. Reloading the same name replaces itself and stays legal.
    private void EnsureDeviceIdUnclaimed(ApiDefinition api)
    {
        foreach (var (name, loaded) in _apis)
        {
            if (loaded.DeviceId == api.DeviceId && name != api.Name)
                throw new InvalidOperationException(
                    $"XAPI device_id {api.DeviceId} is already loaded as API '{name}'; " +
                    $"'{api.Name}' would overwrite its command and event lookups");
        }
    }

    public byte GetDeviceId(string apiName)
    {
        if (!_apis.TryGetValue(apiName, out var api))
            throw new KeyNotFoundException($"API '{apiName}' not loaded");
        return api.DeviceId;
    }

    public CommandDefinition GetCommand(string apiName, string className, string commandName)
    {
        var cls = GetClass(apiName, className);
        var cmd = cls.Commands.FirstOrDefault(c => c.Name == commandName)
            ?? throw new KeyNotFoundException($"Command '{commandName}' not found in {apiName}.{className}");
        return cmd;
    }

    public EventDefinition? FindEvent(byte deviceId, byte classIndex, byte eventIndex)
    {
        return _eventsByHeader.TryGetValue(HeaderKey(deviceId, classIndex, eventIndex), out var entry)
            ? entry.Def
            : null;
    }

    public CommandDefinition? FindCommand(byte deviceId, byte classIndex, byte commandIndex)
    {
        return _commandsByHeader.TryGetValue(HeaderKey(deviceId, classIndex, commandIndex), out var entry)
            ? entry.Def
            : null;
    }

    /// <summary>
    /// The largest payload the definition addressed by this header can describe, or null when no
    /// definition resolves. Variable-length array parameters contribute their maximum, so this is
    /// an upper bound only — never a required size, because a truncated payload still decodes as
    /// far as it goes.
    /// </summary>
    internal int? FindMaxPayloadLength(byte deviceId, byte classIndex, byte index, bool isEvent)
    {
        var key = HeaderKey(deviceId, classIndex, index);
        if (isEvent)
            return _eventsByHeader.TryGetValue(key, out var evt) ? evt.MaxPayload : null;
        return _commandsByHeader.TryGetValue(key, out var cmd) ? cmd.MaxPayload : null;
    }

    private static int HeaderKey(byte deviceId, byte classIndex, byte index)
        => (deviceId << 16) | (classIndex << 8) | index;

    private void Reindex()
    {
        _commandsByHeader.Clear();
        _eventsByHeader.Clear();

        foreach (var api in _apis.Values)
        {
            foreach (var cls in api.Classes)
            {
                foreach (var cmd in cls.Commands)
                {
                    _commandsByHeader[HeaderKey(api.DeviceId, cls.Index, cmd.Index)] =
                        (cmd, BgapiProtocol.MaxPayloadLength(cmd.Returns));
                }

                foreach (var evt in cls.Events)
                {
                    _eventsByHeader[HeaderKey(api.DeviceId, cls.Index, evt.Index)] =
                        (evt, BgapiProtocol.MaxPayloadLength(evt.Parameters));
                }
            }
        }
    }

    public IReadOnlySet<byte> GetKnownDeviceIds()
    {
        return _apis.Values.Select(a => a.DeviceId).ToHashSet();
    }

    public IReadOnlyList<ClassDefinition> FindAllClasses(string apiName)
    {
        if (!_apis.TryGetValue(apiName, out var api))
            throw new KeyNotFoundException($"API '{apiName}' not loaded");
        return api.Classes;
    }

    internal ClassDefinition GetClass(string apiName, string className)
    {
        if (!_apis.TryGetValue(apiName, out var api))
            throw new KeyNotFoundException($"API '{apiName}' not loaded");
        return api.Classes.FirstOrDefault(c => c.Name == className)
            ?? throw new KeyNotFoundException($"Class '{className}' not found in {apiName}");
    }

    private static ApiDefinition ParseApi(XElement root)
    {
        var deviceIdValue = root.Attribute("device_id")?.Value
            ?? throw new InvalidOperationException("XAPI root missing required 'device_id' attribute");
        var deviceId = byte.Parse(deviceIdValue);
        var deviceName = root.Attribute("device_name")?.Value
            ?? throw new InvalidOperationException("XAPI root missing required 'device_name' attribute");

        var dataTypes = new Dictionary<string, DataTypeDefinition>();
        var dtElement = root.Element("datatypes");
        if (dtElement is not null)
        {
            foreach (var dt in dtElement.Elements("datatype"))
            {
                var name = dt.Attribute("name")?.Value ?? "";
                var baseName = dt.Attribute("base")?.Value ?? "";
                var length = int.Parse(dt.Attribute("length")?.Value ?? "0");
                dataTypes[name] = new DataTypeDefinition(name, baseName, length);
            }
        }

        var classes = new List<ClassDefinition>();
        foreach (var classEl in root.Elements("class"))
        {
            classes.Add(ParseClass(classEl, deviceName, dataTypes));
        }

        if (classes.Count == 0)
            throw new InvalidOperationException($"XAPI '{deviceName}' defines no classes");

        return new ApiDefinition(deviceName, deviceId, dataTypes, classes);
    }

    private static ClassDefinition ParseClass(XElement classEl, string apiName, Dictionary<string, DataTypeDefinition> dataTypes)
    {
        var index = byte.Parse(classEl.Attribute("index")?.Value ?? "0");
        var name = classEl.Attribute("name")?.Value ?? "";

        var commands = new List<CommandDefinition>();
        var events = new List<EventDefinition>();

        foreach (var cmdEl in classEl.Elements("command"))
        {
            var cmdIndex = byte.Parse(cmdEl.Attribute("index")?.Value ?? "0");
            var cmdName = cmdEl.Attribute("name")?.Value ?? "";
            var noReturn = cmdEl.Attribute("no_return")?.Value == "true";

            var parameters = ParseParameters(cmdEl.Element("params"), dataTypes);
            var returns = noReturn ? [] : ParseParameters(cmdEl.Element("returns"), dataTypes);

            commands.Add(new CommandDefinition(
                cmdName, cmdIndex, index, $"{apiName}_cmd_{name}_{cmdName}", parameters, returns, noReturn));
        }

        foreach (var evtEl in classEl.Elements("event"))
        {
            var evtIndex = byte.Parse(evtEl.Attribute("index")?.Value ?? "0");
            var evtName = evtEl.Attribute("name")?.Value ?? "";

            var parameters = ParseParameters(evtEl.Element("params"), dataTypes);

            events.Add(new EventDefinition(
                evtName, evtIndex, index, $"{apiName}_evt_{name}_{evtName}", parameters));
        }

        return new ClassDefinition(name, index, commands, events);
    }

    private static List<XapiParameter> ParseParameters(XElement? paramsEl, Dictionary<string, DataTypeDefinition> dataTypes)
    {
        if (paramsEl is null) return [];

        var parameters = new List<XapiParameter>();
        foreach (var paramEl in paramsEl.Elements("param"))
        {
            var name = paramEl.Attribute("name")?.Value ?? "";
            var dataType = paramEl.Attribute("datatype")?.Value ?? paramEl.Attribute("type")?.Value ?? "";

            var resolvedType = dataType;
            var length = 0;
            if (dataTypes.TryGetValue(dataType, out var dtDef))
            {
                resolvedType = dtDef.BaseType;
                length = dtDef.Length;
            }

            parameters.Add(new XapiParameter(name, dataType, resolvedType, length));
        }

        return parameters;
    }
}

public record ApiDefinition(
    string Name,
    byte DeviceId,
    Dictionary<string, DataTypeDefinition> DataTypes,
    List<ClassDefinition> Classes);

public record ClassDefinition(
    string Name,
    byte Index,
    List<CommandDefinition> Commands,
    List<EventDefinition> Events);

public record CommandDefinition(
    string Name,
    byte Index,
    byte ClassIndex,
    string FullName,
    List<XapiParameter> Parameters,
    List<XapiParameter> Returns,
    bool NoReturn = false);

public record EventDefinition(
    string Name,
    byte Index,
    byte ClassIndex,
    string FullName,
    List<XapiParameter> Parameters);

public record XapiParameter(
    string Name,
    string DataType,
    string ResolvedType,
    int Length);

public record DataTypeDefinition(
    string Name,
    string BaseType,
    int Length);
