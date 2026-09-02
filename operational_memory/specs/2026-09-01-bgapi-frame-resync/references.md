# BGAPI serial frame resync — references

<!-- Links to CM entries, tickets, PRs, docs consulted while shaping. -->

## CM consulted

- `projects/silabs-bgapi-csharp/components/serial-transport.md` — `BgapiConnector`'s framing
  contract, the junk-byte resync rule, `ReadExact`'s retry policy, and the "Where to change what"
  pointers this spec follows. Also its own note that no unit test exists for this component.
- `projects/silabs-bgapi-csharp/components/device-control.md` — `BgapiDevice`'s reader loop and
  stale-response rejection.
- `projects/silabs-bgapi-csharp/components/protocol.md` — the codec and `BgapiHeader.Parse`.
- `projects/silabs-bgapi-csharp/standards/index.md` — the applicability list behind `standards.md`.
- `projects/silabs-bgapi-csharp/standards/resilience/retry-until-transient-status.md` — read in
  full; it is why the `SlStatus.Timeout` fix is a Hub change, not a library change.

## Incident

- Hub firmware job 158, 2026-09-01 09:13:49–09:14:09. Block 192 of 479 (40%).
  `Dropping stale response dev=5 cls=0 idx=0 — in-flight command is dev=5 cls=40 idx=5`, then
  `btmesh.mbt_client.query_block_status timed out after 15000ms`, then `BLOB transfer failed`.

## Follow-up in another repo

- **canvas-hub** — `src/CanvasHub.Infrastructure/Firmware/BlobTransferClient.cs`:
  `RetryCmdErrorCodes` (line 58) is `[Busy, NoMoreResource]`, so the `SlStatus.Timeout` that
  `SendCommandAsync` synthesises on a lost response makes `RetryUntilAsync` throw immediately and
  aborts the whole transfer. The chunk path already absorbs `Timeout` (around line 808); the
  `QueryBlockStatusAsync` path (line 836, retry set applied at line 901) does not. The fix belongs
  there, in that repo's own OM. This library change reduces how often a response is lost; it does
  not make a lost one survivable.

## Memory gaps

- memory gap: `serial-transport.md` records that a short read yields `null`, but not that the
  bytes already consumed are discarded and the stream is therefore left mid-frame. The desync
  consequence — the actual defect — is absent from memory. Had to read
  `src/CsharpBgapi/Serial/BgapiConnector.cs:141-166`.
- memory gap: no CM entry records that the resync heuristic validates only the device-id nibble,
  and so accepts roughly one random byte in eight as a frame start. Had to read the first-byte
  loop, `BgapiConnector.cs:119-138`.
- memory gap: the btmesh class-index map is not in memory, so "is `cls=0` a valid btmesh class?" —
  the question the whole diagnosis turned on — could not be answered from the CM. Had to read
  `src/CsharpBgapi/Xapi/sl_btmesh.xapi`.
- memory gap: nothing records that `BgapiMessage.ClassIndex`/`CommandIndex` come from the raw
  header rather than a resolved definition — the fact that proves the frame was garbage. Had to
  read `src/CsharpBgapi/Protocol/BgapiMessage.cs:16-17`.
- memory gap (cross-project): canvas-hub's `BlobTransferClient` retry-error-code set is not in the
  CM. Had to read Hub's source to establish why the transfer aborted rather than retried.
