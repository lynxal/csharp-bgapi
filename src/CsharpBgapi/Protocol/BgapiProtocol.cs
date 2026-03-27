using System.Buffers.Binary;

namespace CsharpBgapi.Protocol;

/// <summary>
/// BGAPI binary protocol encoder/decoder.
/// Handles command serialization and event deserialization using XAPI definitions.
/// </summary>
public sealed class BgapiProtocol
{
    private readonly XapiDefinitions _definitions;

    public BgapiProtocol(XapiDefinitions definitions)
    {
        _definitions = definitions;
    }

    public byte[] EncodeCommand(string apiName, string className, string commandName, Dictionary<string, object>? parameters = null)
    {
        var cmdDef = _definitions.GetCommand(apiName, className, commandName);
        var deviceId = _definitions.GetDeviceId(apiName);
        var classIndex = cmdDef.ClassIndex;
        var commandIndex = cmdDef.Index;

        var payloadBytes = EncodeParameters(cmdDef.Parameters, parameters ?? []);
        var header = BgapiHeader.CreateCommand(deviceId, classIndex, commandIndex, (ushort)payloadBytes.Length);

        var result = new byte[BgapiHeader.Size + payloadBytes.Length];
        header.WriteTo(result);
        payloadBytes.CopyTo(result, BgapiHeader.Size);
        return result;
    }

    public BgapiMessage DecodeMessage(ReadOnlySpan<byte> data)
    {
        var header = BgapiHeader.Parse(data);
        var payload = data.Slice(BgapiHeader.Size, header.PayloadLength).ToArray();

        string? eventName = null;
        Dictionary<string, object>? parameters = null;

        if (header.IsEvent)
        {
            var evtDef = _definitions.FindEvent(header.DeviceId, header.ClassIndex, header.CommandIndex);
            if (evtDef is not null)
            {
                eventName = evtDef.FullName;
                parameters = DecodeParameters(evtDef.Parameters, payload);
            }
        }
        else
        {
            var cmdDef = _definitions.FindCommand(header.DeviceId, header.ClassIndex, header.CommandIndex);
            if (cmdDef is not null)
            {
                eventName = cmdDef.FullName + "_response";
                parameters = DecodeParameters(cmdDef.Returns, payload);
            }
        }

        return new BgapiMessage(header, payload)
        {
            EventName = eventName,
            Parameters = parameters
        };
    }

    /// <summary>
    /// Finds the name of the parameter with errorcode type in a command's return parameters.
    /// Returns null if no errorcode parameter exists.
    /// </summary>
    public string? FindErrorCodeParamName(string apiName, string className, string commandName)
    {
        var cmdDef = _definitions.GetCommand(apiName, className, commandName);
        return FindErrorCodeParamName(cmdDef);
    }

    internal static string? FindErrorCodeParamName(CommandDefinition cmdDef)
    {
        foreach (var param in cmdDef.Returns)
        {
            if (param.DataType == "errorcode" || param.ResolvedType == "errorcode")
                return param.Name;
        }
        return null;
    }

    private static byte[] EncodeParameters(IReadOnlyList<XapiParameter> paramDefs, Dictionary<string, object> values)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        foreach (var paramDef in paramDefs)
        {
            if (!values.TryGetValue(paramDef.Name, out var value))
                value = GetDefaultValue(paramDef.ResolvedType);

            WriteValue(writer, paramDef.ResolvedType, paramDef.Length, value);
        }

        return ms.ToArray();
    }

    private static Dictionary<string, object> DecodeParameters(IReadOnlyList<XapiParameter> paramDefs, ReadOnlySpan<byte> data)
    {
        var result = new Dictionary<string, object>();
        int offset = 0;

        foreach (var paramDef in paramDefs)
        {
            if (offset >= data.Length) break;
            var (value, bytesRead) = ReadValue(data[offset..], paramDef.ResolvedType, paramDef.Length);
            result[paramDef.Name] = value;
            offset += bytesRead;
        }

        return result;
    }

    private static void WriteValue(BinaryWriter writer, string dataType, int length, object value)
    {
        switch (dataType)
        {
            case "uint8":
                writer.Write(Convert.ToByte(value));
                break;
            case "uint16":
            case "errorcode":
                writer.Write(Convert.ToUInt16(value));
                break;
            case "uint32":
                writer.Write(Convert.ToUInt32(value));
                break;
            case "int8":
                writer.Write(Convert.ToSByte(value));
                break;
            case "int16":
                writer.Write(Convert.ToInt16(value));
                break;
            case "int32":
                writer.Write(Convert.ToInt32(value));
                break;
            case "uint64":
                writer.Write(Convert.ToUInt64(value));
                break;
            case "int64":
                writer.Write(Convert.ToInt64(value));
                break;
            case "bd_addr":
                var addr = (byte[])value;
                writer.Write(addr, 0, 6);
                break;
            case "hw_addr":
                var hwAddr = (byte[])value;
                writer.Write(hwAddr, 0, 6);
                break;
            case "ipv4":
                var ipBytes = (byte[])value;
                writer.Write(ipBytes, 0, 4);
                break;
            case "sl_bt_uuid_16_t":
                var uuid16 = (byte[])value;
                writer.Write(uuid16, 0, 2);
                break;
            case "sl_bt_uuid_64_t":
                var uuid64 = (byte[])value;
                writer.Write(uuid64, 0, 8);
                break;
            case "uint8array":
            case "byte_array":
                var bytes = (byte[])value;
                // Always variable-length: 1-byte length prefix + data
                writer.Write((byte)bytes.Length);
                writer.Write(bytes);
                break;
            case "uuid_128":
            case "aes_key_128":
                var fixedBytes = (byte[])value;
                writer.Write(fixedBytes, 0, Math.Min(fixedBytes.Length, length));
                for (int i = fixedBytes.Length; i < length; i++)
                    writer.Write((byte)0);
                break;
            case "uint16array":
                var u16bytes = (byte[])value;
                // 2-byte little-endian length prefix
                writer.Write((ushort)u16bytes.Length);
                writer.Write(u16bytes);
                break;
            default:
                if (value is byte[] raw)
                {
                    if (length == 0)
                    {
                        writer.Write((byte)raw.Length);
                        writer.Write(raw);
                    }
                    else
                    {
                        writer.Write(raw, 0, Math.Min(raw.Length, length));
                    }
                }
                else
                {
                    writer.Write(Convert.ToUInt32(value));
                }
                break;
        }
    }

    private static (object value, int bytesRead) ReadValue(ReadOnlySpan<byte> data, string dataType, int length)
    {
        switch (dataType)
        {
            case "uint8":
                return (data[0], 1);
            case "uint16":
            case "errorcode":
                return (BinaryPrimitives.ReadUInt16LittleEndian(data), 2);
            case "uint32":
                return (BinaryPrimitives.ReadUInt32LittleEndian(data), 4);
            case "int8":
                return ((sbyte)data[0], 1);
            case "int16":
                return (BinaryPrimitives.ReadInt16LittleEndian(data), 2);
            case "int32":
                return (BinaryPrimitives.ReadInt32LittleEndian(data), 4);
            case "uint64":
                return (BinaryPrimitives.ReadUInt64LittleEndian(data), 8);
            case "int64":
                return (BinaryPrimitives.ReadInt64LittleEndian(data), 8);
            case "bd_addr":
                return (data[..6].ToArray(), 6);
            case "hw_addr":
                return (data[..6].ToArray(), 6);
            case "ipv4":
                return (data[..4].ToArray(), 4);
            case "sl_bt_uuid_16_t":
                return (data[..2].ToArray(), 2);
            case "sl_bt_uuid_64_t":
                return (data[..8].ToArray(), 8);
            case "uuid_128":
            case "aes_key_128":
                return (data[..16].ToArray(), 16);
            case "uint8array":
            case "byte_array":
                {
                    // Always variable-length: 1-byte length prefix + data
                    int len = data[0];
                    return (data.Slice(1, len).ToArray(), 1 + len);
                }
            case "uint16array":
                {
                    // 2-byte little-endian length prefix
                    int len = BinaryPrimitives.ReadUInt16LittleEndian(data);
                    return (data.Slice(2, len).ToArray(), 2 + len);
                }
            default:
                if (length > 0)
                    return (data[..length].ToArray(), length);
                if (data.Length >= 4)
                    return (BinaryPrimitives.ReadUInt32LittleEndian(data), 4);
                return (data.ToArray(), data.Length);
        }
    }

    private static object GetDefaultValue(string dataType) => dataType switch
    {
        "uint8" or "int8" => (byte)0,
        "uint16" or "int16" or "errorcode" => (ushort)0,
        "uint32" or "int32" => (uint)0,
        "uint64" or "int64" => (ulong)0,
        _ => Array.Empty<byte>()
    };
}
