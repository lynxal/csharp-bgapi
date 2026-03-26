using SilabsBgapi.Protocol;

namespace SilabsBgapi.Commands;

/// <summary>
/// Helper for building BGAPI commands with a fluent API.
/// </summary>
public sealed class CommandBuilder
{
    private readonly BgapiProtocol _protocol;
    private string _apiName = "";
    private string _className = "";
    private string _commandName = "";
    private readonly Dictionary<string, object> _parameters = new();

    public CommandBuilder(BgapiProtocol protocol)
    {
        _protocol = protocol;
    }

    public CommandBuilder Api(string apiName)
    {
        _apiName = apiName;
        return this;
    }

    public CommandBuilder Class(string className)
    {
        _className = className;
        return this;
    }

    public CommandBuilder Command(string commandName)
    {
        _commandName = commandName;
        return this;
    }

    public CommandBuilder Param(string name, object value)
    {
        _parameters[name] = value;
        return this;
    }

    public byte[] Build()
    {
        if (string.IsNullOrEmpty(_apiName) || string.IsNullOrEmpty(_className) || string.IsNullOrEmpty(_commandName))
            throw new InvalidOperationException("Api, Class, and Command must be set before building");

        return _protocol.EncodeCommand(_apiName, _className, _commandName, _parameters);
    }
}
