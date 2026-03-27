# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
- Configurable options via `SilabsBgapiOptions` (13 tunable parameters)
- DI registration via `AddSilabsBgapi()` extension method
- Support for `IOptions<T>`, `ILogger<T>`, and `ILoggerFactory`
- Multi-target: net9.0 and net10.0
- SourceLink for debugger source stepping
