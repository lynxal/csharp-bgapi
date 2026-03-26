# SilabsBgapi

[![NuGet](https://img.shields.io/nuget/v/SilabsBgapi.svg)](https://www.nuget.org/packages/SilabsBgapi)
[![Build](https://github.com/Lynxal/silabs-bgapi-csharp/actions/workflows/ci.yml/badge.svg)](https://github.com/Lynxal/silabs-bgapi-csharp/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

Full-featured C# library for Silicon Labs [BGAPI](https://docs.silabs.com/bluetooth/latest/bluetooth-stack-api/) serial communication with NCP (Network Co-Processor) devices. Provides command/response framing, event selectors with parameter matching, retry logic, and XAPI-driven protocol definitions for Bluetooth and Bluetooth Mesh.

Ported from the Python [BGLib/pyBGAPI](https://github.com/SiliconLabs/pybgapi) patterns, this is the first full-featured .NET BGAPI library.

## Architecture

```
BgapiDevice (main entry point)
+-- BgapiConnector   -- Serial port I/O with framing and device ID validation
+-- BgapiProtocol    -- BGAPI message encoding/decoding from XAPI definitions
+-- BgapiEventQueue  -- Selector-based event waiting with retry support
```

- **BgapiDevice** orchestrates the connector, protocol, and event queue. It runs a background reader thread that dispatches responses and events.
- **BgapiConnector** handles raw serial communication with thread-safe send/receive locks.
- **BgapiProtocol** encodes commands and decodes messages using XAPI XML definitions.
- **BgapiEventQueue** provides `WaitEvents` (selector-based event matching) and `RetryUntilAsync` (command retry with event confirmation).

## Prerequisites

- **.NET 9.0** or later
- **Silicon Labs NCP hardware** (e.g., EFR32 series with Bluetooth/Bluetooth Mesh firmware)
- **Silicon Labs Gecko SDK** (for XAPI definition files -- see [XAPI Setup](#xapi-setup))

## Installation

```shell
dotnet add package SilabsBgapi
```

## XAPI Setup

XAPI definition files describe the BGAPI protocol surface (commands, events, parameters). These files are part of the Silicon Labs Gecko SDK and are **not included** in this package due to licensing restrictions.

### Obtaining XAPI Files

1. Install [Silicon Labs Gecko SDK](https://github.com/SiliconLabs/gecko_sdk) (via Simplicity Studio or standalone)
2. Locate the XAPI files in your SDK installation:
   - **Bluetooth:** `<gecko_sdk>/protocol/bluetooth/api/sl_bt.xapi`
   - **Bluetooth Mesh:** `<gecko_sdk>/protocol/bluetooth/api/sl_btmesh.xapi`
3. Copy them to your project or configure the path at runtime

### Loading XAPI Files

```csharp
device.LoadXapi("/path/to/sl_bt.xapi");
device.LoadXapi("/path/to/sl_btmesh.xapi");
```

> **Note:** Use XAPI files that match your NCP firmware version for correct protocol definitions.

## Quick Start

```csharp
using SilabsBgapi;

var device = new BgapiDevice();
device.LoadXapi("sl_bt.xapi");
device.Open("/dev/ttyACM0"); // or "COM3" on Windows

var response = await device.SendCommandAsync("bt", "system", "hello");
Console.WriteLine($"Status: {response.Status}");

device.Close();
```

## Configuration

All tunable parameters are exposed via `SilabsBgapiOptions`:

| Property | Default | Description |
|----------|---------|-------------|
| `DefaultBaudRate` | 115200 | Default baud rate for serial port communication |
| `SerialReadTimeoutMs` | 1000 | Serial port read timeout (ms) |
| `SerialWriteTimeoutMs` | 1000 | Serial port write timeout (ms) |
| `ReadExactMaxRetries` | 5 | Max retries for partial read timeouts |
| `ResponseTimeoutSeconds` | 2.0 | Default timeout for command responses |
| `ReaderLoopReadTimeoutMs` | 100 | Read timeout for background reader loop (ms) |
| `StopReaderTimeoutSeconds` | 2.0 | Timeout for stopping the reader thread |
| `WaitEventsDefaultMaxTimeSeconds` | 10.0 | Default max time for WaitEvents |
| `RetryMax` | 5 | Max outer retries in RetryUntilAsync |
| `RetryIntervalSeconds` | 1.0 | Interval between outer retries |
| `RetryCmdMax` | 6 | Max command-level retries for transient errors |
| `RetryCmdIntervalSeconds` | 1.0 | Interval between command retries |

## Dependency Injection

### Basic

```csharp
services.AddSilabsBgapi();
```

### With Configuration Binding

```csharp
services.Configure<SilabsBgapiOptions>(configuration.GetSection("SilabsBgapi"));
services.AddSilabsBgapi();
```

### With Inline Configuration

```csharp
services.AddSilabsBgapi(options =>
{
    options.ResponseTimeoutSeconds = 5.0;
    options.RetryMax = 10;
});
```

### appsettings.json

```json
{
  "SilabsBgapi": {
    "DefaultBaudRate": 115200,
    "SerialReadTimeoutMs": 1000,
    "SerialWriteTimeoutMs": 1000,
    "ReadExactMaxRetries": 5,
    "ResponseTimeoutSeconds": 2.0,
    "ReaderLoopReadTimeoutMs": 100,
    "StopReaderTimeoutSeconds": 2.0,
    "WaitEventsDefaultMaxTimeSeconds": 10.0,
    "RetryMax": 5,
    "RetryIntervalSeconds": 1.0,
    "RetryCmdMax": 6,
    "RetryCmdIntervalSeconds": 1.0
  }
}
```

## Usage Examples

### Wait for Events

```csharp
var selector = new EventSelector("bt", "mesh", "vendor_model_receive");
var events = device.WaitEvents(selector, TimeSpan.FromSeconds(5), finalEventCount: 3);
```

### Retry Pattern

```csharp
var events = await device.RetryUntilAsync(
    command: () => device.SendCommandAsync("bt", "mesh", "vendor_model_send", parameters),
    eventSelector: new EventSelector("bt", "mesh", "vendor_model_receive"),
    retryParams: new RetryParams { RetryMax = 3, RetryInterval = TimeSpan.FromSeconds(2) },
    finalEventCount: 1);
```

### Subscribe to Events

```csharp
device.Subscribe("evt_mesh_vendor_model_receive", message =>
{
    Console.WriteLine($"Received vendor event: {message.EventName}");
});
```

## API Reference

| Class | Responsibility |
|-------|---------------|
| `BgapiDevice` | Main entry point -- open/close, send commands, wait events, subscribe |
| `BgapiConnector` | Serial port I/O with framing and device ID validation |
| `BgapiProtocol` | BGAPI message encoding/decoding |
| `BgapiEventQueue` | Event queue with selector-based waiting and retry logic |
| `XapiDefinitions` | XAPI XML definition loading and lookup |
| `EventSelector` | Event matching criteria for WaitEvents |
| `SilabsBgapiOptions` | Configuration POCO for all tunable parameters |
| `SilabsBgapiServiceExtensions` | DI registration extension methods |
| `SlStatus` | Silicon Labs status/error code enum (275+ codes) |
| `CommandBuilder` | Fluent command builder for constructing BGAPI commands |

## Contributing

Contributions are welcome! Please see [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

## License

This project is licensed under the MIT License -- see [LICENSE](LICENSE) for details.

**Note:** XAPI definition files (`sl_bt.xapi`, `sl_btmesh.xapi`) are Silicon Labs intellectual property under the [Gecko SDK MSLA](https://www.silabs.com/about-us/legal/master-software-license-agreement) and are not included in this repository or NuGet package. Obtain them separately from the [Gecko SDK](https://github.com/SiliconLabs/gecko_sdk).
