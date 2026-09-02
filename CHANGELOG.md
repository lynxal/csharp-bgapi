# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Built-in XAPI definitions (Bluetooth 10.1.1, Bluetooth Mesh 10.1.1) embedded as assembly resources
- `BgapiDevice.LoadDefaultXapis()` to load built-in XAPI definitions without external files
- `XapiDefinitions.HasDefinitions` property to check if definitions are loaded
- `XapiDefinitions.LoadedApiNames` property to list loaded API names
- `loadDefaultXapis` parameter on `AddCsharpBgapi()` DI extension method

### Changed

- **Breaking:** `AddCsharpBgapi()` now loads built-in XAPI definitions by default (`loadDefaultXapis` changed from `false` to `true`). Pass `loadDefaultXapis: false` to opt out.
- **Breaking:** `BgapiConnector.FindSilabsPorts()` renamed to `FindPorts()`. It never filtered by vendor — it returns every serial port on the machine, and the new name says so.
- **Breaking:** `CommandBuilder.Build()` now clears the builder's api, class, command and parameters. A builder is configured once per command; a second `Build()` throws `InvalidOperationException` instead of reusing the previous state.

### Fixed

- `RetryParams.RetryCmdMax` defaulted to `10` while `CsharpBgapiOptions.RetryCmdMax` defaults to `6`, so a hand-built `RetryParams` silently took a retry budget nobody configured. The record's defaults now match the options.
- A `CommandBuilder` reused for a second command silently reused any parameter of the same name from the first.
- Loading an XAPI whose `device_id` is already claimed by another loaded API silently overwrote that API's command and event lookups (last loaded won), decoding frames under the wrong definition and misinforming the frame-resync plausibility check. `LoadFromFile`/`LoadFromStream` now throw `InvalidOperationException`; reloading the same API name still replaces itself.

## [0.1.0] - 2026-03-26

### Added

- BGAPI serial communication with NCP devices via `BgapiDevice`
- XAPI-driven protocol encoding/decoding (`BgapiProtocol`, `XapiDefinitions`)
- Thread-safe serial I/O with device ID validation (`BgapiConnector`)
- Event selector system with parameter matching (`EventSelector`, `BgapiEventQueue`)
- `WaitEvents` for selector-based event waiting with timeout
- `RetryUntilAsync` for command retry with event confirmation
- Event subscription via `Subscribe`/`Unsubscribe`
- Fluent command builder (`CommandBuilder`)
- Full Silicon Labs status code enum (`SlStatus`, 275+ codes)
- Configurable options via `CsharpBgapiOptions` (13 tunable parameters)
- DI registration via `AddCsharpBgapi()` extension method
- Support for `IOptions<T>`, `ILogger<T>`, and `ILoggerFactory`
- Multi-target: net9.0 and net10.0
- SourceLink for debugger source stepping
