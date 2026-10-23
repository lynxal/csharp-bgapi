# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.1.0] - Unreleased

### Added

- `WaitEventsAsync` and `WaitEventAsync` on `BgapiEventQueue` and `BgapiDevice`: the same parameters, stop conditions and exceptions as `WaitEvents` and `WaitEvent`, but the wait for the next event is awaited instead of blocking a thread. `WaitEvents` still blocks the caller's own thread and nothing else.

### Changed

- `RetryUntilAsync` no longer holds a thread while it waits for events. It waited through the blocking `WaitEvents`, so with a command that completed synchronously it held the caller's thread for the whole `RetryInterval` of every attempt before returning a task.

### Fixed

- A wait woken by an event that arrived in time now takes that event even when the waiter resumes after its deadline. It used to stop at the deadline and leave the event queued, so `RetryUntilAsync` reported a timeout and re-sent a command whose reply had already arrived.
- A wait whose deadline was overrun by exactly 1 ms no longer waits without a bound. The overrun equalled `Timeout.InfiniteTimeSpan`, so the wait lasted until the next event arrived, and `GetEvent` on a quiet bus never returned.

## [1.0.0] - Unreleased

First stable release. From here the public API follows Semantic Versioning: breaking changes wait for 2.0.0.

### Added

- `BgapiDevice.FindPorts()` and `BgapiDevice.DroppedByteCount`, the two `BgapiConnector` members worth keeping public.
- The `Subscribe` handler contract is documented. Handlers run in order on the device's only reader thread, and nothing is read off the port until a handler returns. A synchronous `SendCommand` from a handler returns `SlStatus.Timeout`, because its response cannot be read until the handler returns. Only `no_return` commands return `OK`. Calling a blocking method from a handler is unsupported; hand the work to another thread. The behaviour itself is unchanged.

### Changed

- **Breaking:** the package targets `net10.0` only. `net9.0` is dropped; its support ends on 2026-11-10.
- **Breaking:** `BgapiConnector` is internal. Its `SetKnownDeviceIds` and `ReadMessage` bypass the device's reader thread and command lock, and freezing them under SemVer would rule out reworking the serial layer in 1.x. Use `BgapiDevice.FindPorts()` and `BgapiDevice.DroppedByteCount` instead.
- **Breaking:** `BgapiDevice.Open` throws `InvalidOperationException` when the device is already open. A second `Open` used to replace the port without closing it and start a second reader thread on the same connector, so two readers raced for frames.
- **Breaking:** `BgapiDevice.Open` also throws `InvalidOperationException` while the previous session's reader is still running — a subscriber callback held it past `StopReaderTimeoutSeconds`. Retry once the callback has returned. The reader used to be forgotten and run beside the new one.
- After a command times out, or its caller cancels once it was sent, the next command with the same header is preceded by `bt.system.hello` or `bt.system.get_version`, whichever is not the repeat and has no unanswered send of its own. The NCP answers in order, so the fence's answer proves the late one has passed. If the fence goes unanswered, the command returns `SlStatus.Timeout` without being sent, so such a repeat can take up to twice its timeout. Retrying through an outage keeps fencing, so the command goes out again once the NCP answers. After such retries, a second fence can be needed. An answered command whose header had no unanswered send clears the need for a fence, so a caller that moves on to another command sends nothing extra.
- CI prerelease packages built after a release tag take the next version as their base (`1.0.1-ci.N` after `v1.0.0`, or the csproj's `AssemblyVersion` when it is already ahead), so they sort above the release they follow. They used to be `X.Y.Z-ci.N`, which SemVer orders below `X.Y.Z`. `N` is the total commit count, not the height from the nearest tag, so a new tag (a hotfix on main, say) no longer restarts it below builds already published.

### Fixed

- `$name` substitution in event selectors never ran. `WaitEvents` accepted `paramSubs` and ignored them, so a constraint such as `"source_address": "$address"` kept the literal string `"$address"` and a `RetryUntilAsync` wait for the event named by the command's response could only time out. `WaitEvents`, `RetryUntilAsync`'s `retryEventSelector` and `EventSelector.Build` given an already-built selector now resolve it against `paramSubs`, and `RetryUntilAsync` keeps the last successful command's values for the waits of later attempts. Substitutions given when the selector was built win over the response's. Selectors subclassed outside the library are matched as written.
- Building a `NameParamSelector` with `paramSubs` wrote the substituted values into the caller's own `EventParamValues` instance.
- `NameParamSelector` constraints and `#unique` compared values with boxed `Equals`, which needs the same type and compares arrays by reference: an `int` constraint never matched the `ushort` an event decodes to, and a `bd_addr`, uuid or byte array never matched or deduplicated. Integers now compare by value and byte arrays by content.
- After a `RetryUntilAsync` wait fell short, the next attempt restarted the final-event count at 0 while keeping the events already counted, so a stateful (`#unique`) selector could never reach `finalEventCount` and every retry failed.
- `RetryUntilAsync` carried the failures its `retryEventSelector` matched into the next attempt: a wait that fell short was never checked for them, and a stateful selector's retry kept them too. They still counted toward `finalEventCount`, and under `#unique` they hid their node's later success, so a node that reported failure could complete the result. They are now dropped before the retry, each with the count it took. The last attempt still returns what it collected, failures included, as Python's `retry_until` does.
- A command response whose payload ends before its errorcode reported `SlStatus.OK`; it now reports `SlStatus.Fail`.
- `AddCsharpBgapi` silently ignored its configure delegate when the host registered no logging: the device fell back to a constructor that builds default options. It now calls the options constructor itself, whatever logging the host registers.
- `BgapiDevice.Close` on a port whose USB device was removed threw and left the port reporting open, so every later `Open` threw until the process restarted. The port is now abandoned and its handle released.
- `Open` after the port went away without `Close()` left the old reader thread running beside the new one.
- A late response to a timed-out command completed the next command with the same header, which reported that response's status and parameters even when the NCP never answered it. A BGAPI response identifies its command only by header; see the fence under Changed.
- Two concurrent `Open` calls could both open a port: the second overwrote the first's port and reader, and the first port stayed open with nothing referencing it. `Open`, `Close` and `Dispose` are now serialized.
- A reader held up after a read (by a subscriber callback) for longer than `StopReaderTimeoutSeconds` dispatched the closed port's message after `Close` returned, into the queue and to subscribers of a reopened session. A message read after `Close` is now dropped.
- `Dispose` with a command in flight made that command throw `ObjectDisposedException` from its lock instead of its cancellation or timeout, and a caller waiting for the lock was never released. The command in flight now completes as after `Close`; waiting and later callers get `ObjectDisposedException`.
- After a final `Close` or `Dispose`, the late fault of the serial read still in flight surfaced as `TaskScheduler.UnobservedTaskException`. Only a reopen used to observe it.
- The package stored its XAPI files as `xapi//sl_bt.xapi` and `xapi//sl_btmesh.xapi`, with an empty path segment, when packed on Linux, as every published package was. They are now `xapi/sl_bt.xapi` and `xapi/sl_btmesh.xapi`.

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
