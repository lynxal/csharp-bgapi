---
title: BGAPI serial frame resync
type: spec
status: active
author: Sipan
description: Stop BgapiConnector.ReadMessage from desyncing the serial stream — buffer partial frames instead of discarding them, validate headers against the XAPI, resync one byte at a time, and log it at Warning
updated: 2026-09-01
---

# BGAPI serial frame resync — shape

## Scope

Make the reader recover from a desynced serial line instead of turning one lost byte into a
run of corrupted frames, and make it say so in production logs.

Three changes, all in this repo:

1. **`src/CsharpBgapi/Serial/BgapiConnector.cs`** — a persistent RX accumulator plus a pure,
   `internal static` frame extractor. A truncated frame's bytes stay buffered for the next
   poll rather than being discarded. A header is accepted only when the XAPI resolves it; a
   rejected header drops exactly one byte and rescans. Resync is logged at `LogWarning` with
   a monotonic dropped-byte counter.
2. **`src/CsharpBgapi/Protocol/BgapiProtocol.cs`** — a small `IsKnownHeader(in BgapiHeader)`
   so the extractor can validate without the connector reaching into `XapiDefinitions`.
3. **`src/CsharpBgapi/BgapiDevice.cs`** — `HandleResponseMessage` splits the unresolvable-frame
   case (`EventName is null`) out of the genuine stale-response drop, so a framing fault is
   never again read as a late response from a previous command.

Plus `tests/CsharpBgapi.Tests/BgapiConnectorFrameTests.cs` driving the extractor directly —
the first unit test this component has had.

## The defect

Production FW update (Hub firmware job 158, 2026-09-01 09:14:09) died at block 192 of 479.
Visible failure: `btmesh.mbt_client.query_block_status timed out after 15000ms`, preceded by
`Dropping stale response dev=5 cls=0 idx=0 — in-flight command is dev=5 cls=40 idx=5`.

That "stale response" was not a message. `sl_btmesh.xapi` declares btmesh as `device_id="5"`
with class indices starting at 1 — **there is no btmesh class 0**. `BgapiMessage.ClassIndex`
and `CommandIndex` read straight off the raw header bytes, so the reader parsed four garbage
bytes as a header and then consumed that bogus header's payload length from the stream,
swallowing `query_block_status`'s real response.

The stream was out of frame. The identity check that logged the warning is correct and
ratified (`error-handling/reject-stale-command-responses`) — it reported the symptom.

Why the stream stayed out of frame:

- `ReadMessage` returned `null` on a short header read or short payload read and **discarded
  the bytes already consumed**, leaving the port mid-frame. The next call parsed payload
  bytes as a header. `ReadExact` gives up after `ReadExactMaxRetries` (5) × `ReadTimeout`
  (100 ms) = 500 ms, reachable on a loaded Pi mid-FW-update.
- Resync validated only the device-id nibble, so roughly one random byte in eight passes as a
  frame start. A frame whose header resolved to no definition at all was accepted and routed
  anyway.
- Every framing detail was `LogDebug`, and the library's logger runs at Information in the
  Hub — so the desync left no trace beyond its downstream symptom.

## Decisions & rationale

- **Buffer, never discard.** The single change that removes the desync generator: bytes
  already read stay in an accumulator until a frame consumes them. Everything else is
  hardening on top.
- **Validate the header against the XAPI, not just the device-id nibble.** The lookups the
  check needs (`FindCommand(dev, cls, idx)`, `FindEvent(...)`) already exist and are what
  `DecodeMessage` uses — this is the same `protocol-codec/xapi-definition-driven` rule applied
  one step earlier, at frame acceptance rather than at decode.
- **Resync one byte at a time.** A rejected 4-byte window may still contain a real frame start
  at offset 1. Skipping four bytes would step over it.
- **Pure `internal static` extractor.** Keeps the framing logic testable without a physical
  `SerialPort` — the reason the CM records this component as having no unit test. No new public
  type, so the library's dual-constructor and DI-registration conventions are untouched, and
  `CsharpBgapi.Tests` already has `InternalsVisibleTo`.
- **Warning, not Debug, for resync.** A dropped byte on the NCP link is not routine. The
  ordinary "no message this poll" timeout stays at `LogTrace`.
- **`SlStatus.Timeout` stays non-retryable in this library.** Hub's
  `BlobTransferClient.RetryCmdErrorCodes` is `[Busy, NoMoreResource]`, and `RetryUntilAsync`
  throws immediately for anything outside that set — which is why one lost response aborted
  479 blocks. Adding `Timeout` to the library default would contradict the ratified
  `resilience/retry-until-transient-status`; the fix belongs at Hub's call site, where the
  chunk path already tolerates it. Recorded in `references.md` as a follow-up.

## Out of scope

- **Hub-side change** — adding `SlStatus.Timeout` to `BlobTransferClient.RetryCmdErrorCodes`
  (or handling it in `QueryBlockStatusAsync` the way the chunk loop already does). Different
  repo, different OM; see `references.md`.
- **The physical cause of the initial byte loss** — RX overflow while the reader thread is
  blocked in `DispatchToHandlers` (subscribed handlers run on the reader thread), or RTS/CTS
  not actually wired on `/dev/ttyAMA3`. Not decidable from the incident log. The hardening
  here is correct either way, and the new Warning counters are what will identify it next time.
- **`ReadExactMaxRetries` / timeout tuning** — behaviour and the timeout contract are unchanged.
- **`FindSilabsPorts()` returning unfiltered port names** — pre-existing, unrelated (already an
  open question on the CM's `serial-transport` component).
