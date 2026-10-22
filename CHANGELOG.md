# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.3.0] - 2026-10-22

### Added

- `CsharpBgapiOptions.MaxQueuedEvents` (default 1024) — how many events the queue retains for `WaitEvents` consumers that have not taken them yet. Beyond it the oldest unclaimed event is dropped.
- `BgapiEventQueue.Count` and `BgapiEventQueue.DroppedEventCount` — events currently retained, and events evicted by the bound since the queue was created (or last cleared, for the warning episode).
- `WaitEvents` accepts `Timeout.InfiniteTimeSpan` for `timeout` or `maxTime` to drop that bound; `GetEvent` keeps accepting it to block until an event arrives.

### Changed

- **Breaking:** `BgapiEventQueue.WaitEvents` no longer dequeues every event and re-enqueues the non-matching ones at the tail when it returns. Each call scans the queue in place from its own cursor and removes only the events its selector takes; everything else stays where it is, in arrival order, visible to other waiters immediately. A call still finds events that were queued before it started.
- **Breaking:** unclaimed events are no longer retained without limit — see `MaxQueuedEvents`. The first eviction logs at Warning with what to do, later ones at Debug until `Clear()`.
- `WaitEvents` throws `OperationCanceledException` when its token is already cancelled on entry (it already did when cancelled mid-wait), instead of returning or throwing `BgapiWaitEventException` depending on timing.
- `WaitEvents` with `keepAllEvents` now counts the ignored events it takes toward `maxEvents`.

### Fixed

- A `WaitEvents` call for one event type held every other event that arrived during its window and only re-queued them when it returned — with a receive loop and a firmware wait sharing the queue, vendor-model events were delayed by up to a whole firmware chunk wait.
- Re-queued events landed behind everything that had arrived meanwhile, so same-source order was not preserved.
- A `WaitEvents` call cancelled mid-wait threw without re-queuing the events it was holding; they were lost.
- Events no consumer ever selected were re-queued forever and dequeued, categorized and re-queued again by every later `WaitEvents` call — memory and per-call work that grew for the life of the process.

## [0.2.1] - 2026-10-22

### Changed

- The first rejected header of a serial resync episode is logged at Warning (was Debug), beside the drop warning it explains, so a production log shows what was dropped and not only how many bytes.

### Fixed

- `BgapiConnector.ReadMessage` lost bytes on Linux when they arrived as its read timed out: .NET's Unix `SerialStream` reads the fd into a timed-out request and then drops the bytes. The connector now keeps one read in flight and never cancels it — the timeout bounds the wait, and a late read is collected by the next poll.

## [0.2.0] - 2026-09-02

### Added

- Built-in XAPI definitions (Bluetooth 10.1.1, Bluetooth Mesh 10.1.1) embedded as assembly resources
- `BgapiDevice.LoadDefaultXapis()` to load built-in XAPI definitions without external files
- `XapiDefinitions.HasDefinitions` property to check if definitions are loaded
- `XapiDefinitions.LoadedApiNames` property to list loaded API names
- `loadDefaultXapis` parameter on `AddCsharpBgapi()` DI extension method
- `CsharpBgapiOptions.PartialFrameTimeoutMs` (default 500 ms) — how long a partially received frame waits for the rest of its payload before the receive path abandons it and resyncs
- `BgapiProtocol.IsKnownHeader(in BgapiHeader)` — validates a candidate frame header against the loaded XAPI definitions and bounds its declared payload length, so a mid-payload window that happens to resolve cannot claim the bytes that follow it

### Changed

- **Breaking:** `AddCsharpBgapi()` now loads built-in XAPI definitions by default (`loadDefaultXapis` changed from `false` to `true`). Pass `loadDefaultXapis: false` to opt out.
- **Breaking:** `BgapiConnector.FindSilabsPorts()` renamed to `FindPorts()`. It never filtered by vendor — it returns every serial port on the machine, and the new name says so.
- **Breaking:** `CommandBuilder.Build()` now clears the builder's api, class, command and parameters. A builder is configured once per command; a second `Build()` throws `InvalidOperationException` instead of reusing the previous state.
- **Breaking:** `BgapiDevice.AddEventFilter()` and `RemoveEventFilter()` now throw `KeyNotFoundException` when the class or event name does not resolve in the loaded definitions, instead of returning a response for class 0 / event 0.
- `CsharpBgapiOptions.ReadExactMaxRetries` is no longer read — the receive path is bounded by `PartialFrameTimeoutMs` instead. The property is retained so existing configuration still binds.

### Fixed

- `BgapiConnector.ReadMessage` discarded the bytes of a truncated frame, leaving the serial stream mid-frame so the next read parsed payload bytes as a header — one lost byte turned into a run of corrupted frames. Partial frames are now buffered across polls, a candidate header is accepted only when the XAPI resolves it and its declared length is plausible, resync advances one byte at a time, and dropped bytes are logged at Warning with a running count.
- `BgapiDevice.HandleResponseMessage` reported an unresolvable frame as a stale response from a previous command, hiding a framing fault behind a misleading log line. The two cases are now distinct.
- `AddEventFilter()`/`RemoveEventFilter()` silently installed or removed a filter on class 0 / event 0 when the class or event name did not resolve, and returned `OK` — positive confirmation of the wrong action. See the breaking note under Changed.
- `BgapiConnector.SendCommand` bound the payload array to a `{ByteCount}` log placeholder, rendering `Sending command: System.Byte[] bytes` and recording an array where a structured sink expected a number.
- README's "Wait for Events", "Retry Pattern" and "Subscribe to Events" examples used `new EventSelector(...)` — an abstract type with no constructor — and a subscription key missing the device-name prefix, so they did not compile and the handler never fired.
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
- Full Silicon Labs status code enum (`SlStatus`, 249 codes)
- Configurable options via `CsharpBgapiOptions` (12 tunable parameters)
- DI registration via `AddCsharpBgapi()` extension method
- Support for `IOptions<T>`, `ILogger<T>`, and `ILoggerFactory`
- Multi-target: net9.0 and net10.0
- SourceLink for debugger source stepping
