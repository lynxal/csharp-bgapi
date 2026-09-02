using CsharpBgapi.Protocol;

namespace CsharpBgapi.Commands;

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

    /// <summary>
    /// Encodes the configured command and consumes the builder's state: api, class, command and
    /// every parameter are cleared, so a second call throws until they are set again.
    /// </summary>
    /// <remarks>
    /// Build used to leave everything in place, so a builder pointed at a second command silently
    /// reused any parameter of the same name from the first. Clearing only the parameters would be
    /// worse — api/class/command would still resolve, and the next Build would encode a frame with
    /// every parameter defaulted to zero and hand it to the radio. Clearing all of it makes the
    /// misuse hit the guard above instead.
    /// </remarks>
    public byte[] Build()
    {
        if (string.IsNullOrEmpty(_apiName) || string.IsNullOrEmpty(_className) || string.IsNullOrEmpty(_commandName))
            throw new InvalidOperationException("Api, Class, and Command must be set before building");

        var command = _protocol.EncodeCommand(_apiName, _className, _commandName, _parameters);

        _apiName = "";
        _className = "";
        _commandName = "";
        _parameters.Clear();

        return command;
    }
}
