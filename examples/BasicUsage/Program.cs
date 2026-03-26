// Basic usage example for SilabsBgapi.
//
// Prerequisites:
//   - Silicon Labs NCP device connected via serial (e.g., EFR32 dev board)
//   - XAPI definition files from your Gecko SDK installation:
//       <gecko_sdk>/protocol/bluetooth/api/sl_bt.xapi
//       <gecko_sdk>/protocol/bluetooth/api/sl_btmesh.xapi
//
// Usage:
//   dotnet run -- <serial-port> <xapi-path>
//   dotnet run -- /dev/ttyACM0 /opt/gecko_sdk/protocol/bluetooth/api/sl_bt.xapi
//   dotnet run -- COM3 C:\SiliconLabs\gecko_sdk\protocol\bluetooth\api\sl_bt.xapi

using SilabsBgapi;

if (args.Length < 2)
{
    Console.WriteLine("Usage: BasicUsage <serial-port> <xapi-file-path>");
    Console.WriteLine("  Example: BasicUsage /dev/ttyACM0 /path/to/sl_bt.xapi");
    return;
}

var portName = args[0];
var xapiPath = args[1];

using var device = new BgapiDevice();

// Load the XAPI protocol definitions
Console.WriteLine($"Loading XAPI from: {xapiPath}");
device.LoadXapi(xapiPath);

// Open serial connection to the NCP device
Console.WriteLine($"Opening serial port: {portName}");
device.Open(portName);

try
{
    // Send a system hello command to verify communication
    Console.WriteLine("Sending bt.system.hello...");
    var response = await device.SendCommandAsync("bt", "system", "hello");
    Console.WriteLine($"Response status: {response.Status}");

    if (response.Status == SlStatus.OK)
    {
        Console.WriteLine("NCP communication established successfully.");
    }
    else
    {
        Console.WriteLine($"Command failed with status: {response.Status}");
    }
}
finally
{
    device.Close();
    Console.WriteLine("Device closed.");
}
