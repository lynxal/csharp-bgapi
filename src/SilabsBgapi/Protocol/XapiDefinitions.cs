using System.Xml.Linq;

namespace SilabsBgapi.Protocol;

/// <summary>
/// Parses and holds XAPI XML definitions for BGAPI command/event structures.
/// </summary>
public sealed class XapiDefinitions
{
    private readonly Dictionary<string, ApiDefinition> _apis = new();

    public void LoadFromFile(string path)
    {
        var doc = XDocument.Load(path);
        var root = doc.Root ?? throw new InvalidOperationException("Empty XAPI file");
        var api = ParseApi(root);
        _apis[api.Name] = api;
    }

    public void LoadFromStream(Stream stream)
    {
        var doc = XDocument.Load(stream);
        var root = doc.Root ?? throw new InvalidOperationException("Empty XAPI file");
        var api = ParseApi(root);
        _apis[api.Name] = api;
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
        var api = _apis.Values.FirstOrDefault(a => a.DeviceId == deviceId);
        if (api is null) return null;

        var cls = api.Classes.FirstOrDefault(c => c.Index == classIndex);
        if (cls is null) return null;

        return cls.Events.FirstOrDefault(e => e.Index == eventIndex);
    }

    public CommandDefinition? FindCommand(byte deviceId, byte classIndex, byte commandIndex)
    {
        var api = _apis.Values.FirstOrDefault(a => a.DeviceId == deviceId);
        if (api is null) return null;

        var cls = api.Classes.FirstOrDefault(c => c.Index == classIndex);
        if (cls is null) return null;

        return cls.Commands.FirstOrDefault(c => c.Index == commandIndex);
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

    private ClassDefinition GetClass(string apiName, string className)
    {
        if (!_apis.TryGetValue(apiName, out var api))
            throw new KeyNotFoundException($"API '{apiName}' not loaded");
        return api.Classes.FirstOrDefault(c => c.Name == className)
            ?? throw new KeyNotFoundException($"Class '{className}' not found in {apiName}");
    }

    private static ApiDefinition ParseApi(XElement root)
    {
        var deviceId = byte.Parse(root.Attribute("device_id")?.Value ?? "0");
        var deviceName = root.Attribute("device_name")?.Value ?? "";

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
