// Basic usage example for CsharpBgapi.
//
// Prerequisites:
//   - Silicon Labs NCP device connected via serial (e.g., EFR32 dev board)
//
// Usage:
//   dotnet run -- <serial-port>                  (uses built-in XAPI defaults)
//   dotnet run -- <serial-port> <xapi-path>      (uses custom XAPI file)

using CsharpBgapi;

if (args.Length < 1)
{
    Console.WriteLine("Usage: BasicUsage <serial-port> [xapi-file-path]");
    Console.WriteLine("  Example: BasicUsage /dev/ttyACM0");
    Console.WriteLine("  Example: BasicUsage COM3 C:\\path\\to\\sl_bt.xapi");
    return;
}

var portName = args[0];

using var device = new BgapiDevice();

// Load XAPI protocol definitions
if (args.Length >= 2)
{
    var xapiPath = args[1];
    Console.WriteLine($"Loading custom XAPI from: {xapiPath}");
    device.LoadXapi(xapiPath);
}
else
{
    Console.WriteLine("Loading built-in default XAPI definitions...");
    device.LoadDefaultXapis();
}

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
