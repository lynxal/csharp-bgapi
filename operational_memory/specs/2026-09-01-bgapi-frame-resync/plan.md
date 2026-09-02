# BGAPI serial frame resync — plan

## Tasks

- [x] 1. Add `BgapiProtocol.IsKnownHeader(in BgapiHeader)` — `FindEvent` for an event header,
      `FindCommand` for a response header; true only when the definition resolves.
      → verify: existing `BgapiProtocolDecodeTests` still green; new test asserts a btmesh
      `cls=0 idx=0` header is rejected and a real `mbt_client` header accepted.
- [x] 2. Write `tests/CsharpBgapi.Tests/BgapiConnectorFrameTests.cs` FIRST, against the
      not-yet-existing extractor signature. Opening comment names this defect
      (`testing/regression-tests-document-the-defect`). Cases: frame split across two polls;
      the incident's `28 00 00 00` rejected with one byte dropped and the following valid frame
      still found; leading junk resyncs with the right consumed count; truncated payload
      consumes nothing; valid response and valid event both still decode.
      → verify: the suite fails for the right reason before task 3.
- [x] 3. Add the `internal static` frame extractor to `BgapiConnector` — scans a buffer,
      returns the frame plus bytes consumed, or "need more bytes" consuming nothing; a
      rejected header drops exactly one byte and rescans.
      → verify: `BgapiConnectorFrameTests` all green.
- [x] 4. Rewire `ReadMessage` onto a persistent RX accumulator: append what the port gives
      this poll, ask the extractor, keep the remainder. Delete the discard-on-short-read
      paths. Keep the `TimeoutException` → `null` contract and the `LogTrace` line.
      → verify: full suite green; no signature change on `ReadMessage`.
- [x] 5. Log resync at `LogWarning` with a monotonic dropped-byte counter on the connector.
      → verify: a test asserts the counter advances by the number of bytes dropped.
- [x] 6. Split `BgapiDevice.HandleResponseMessage`: `EventName is null` logs a framing/desync
      warning including payload length, distinct from the stale-response drop (whose existing
      message and behaviour stay exactly as they are).
      → verify: `BgapiDeviceTests` green; new test asserts the two cases are logged distinctly
      and that neither completes the pending TCS.
- [x] 7. Full run: `dotnet-test-runner` over `CsharpBgapi.slnx`.
      → verify: 0 failures across all test classes.

## Working state

All seven tasks plus a Codex review round done. `dotnet-test-runner` over `CsharpBgapi.slnx`:
**60 passed, 0 failed** (2026-09-02).

The regression tests were checked against the defect, not just asserted: with
`IsPlausibleFrameStart` temporarily reverted to the old device-id-nibble-only rule,
`TryExtractFrame_IncidentGarbageHeader_...`, `TryExtractFrame_ResyncsOneByteAtATime_...` and
`TryExtractFrame_JunkOnly_...` all fail — the first one by accepting the incident's `cls=0` header
exactly as production did. The fix was then restored and the suite is green.

Everything is uncommitted on branch `fix/bgapi-frame-resync`, for code + spec to commit together.

### Deviations from the task list, worth a look at review

- `ReadExact` was deleted rather than kept: the accumulator makes retrying a blocking read
  pointless, and it had no other caller. `CsharpBgapiOptions.ReadExactMaxRetries` briefly became
  dead as a result; the P1 review fix now consumes it again as the stalled-candidate budget, so
  Hub's `appsettings.json` value is honoured with its original meaning.
- The old `LogDebug("Read header: ...")` line is gone with the code it lived in.
  `BgapiDevice.ReaderLoop` already logs the same fields at Debug for every message it receives, so
  nothing was lost.
- The desync/stale split in `HandleResponseMessage` sits **inside** the identity-mismatch branch,
  not before it. A frame whose header matches the in-flight command is that command's response
  whether or not the definitions can name it — only a mismatch raises the question the
  `EventName is null` test answers. Putting the check first would also have broken three existing
  tests that construct `BgapiMessage` without an `EventName`.

### Review round (Codex, 2026-09-02) — both findings accepted

- [x] **P1 — a stalled frame candidate had no expiry.** A genuine header whose payload byte was
      lost, or whose length bits are corrupt, was retained forever: the next real frame's bytes got
      folded into it, and because the header is genuine the fabricated frame could complete the
      in-flight command with someone else's payload. Corrupt length bits could also claim up to
      2047 bytes and stall frame delivery until that much traffic arrived — the incident's own
      swallowed-response symptom. Fixed with `StalledCandidateBudget`: a candidate that has waited
      longer than `ReadExactMaxRetries × ReaderLoopReadTimeoutMs` (500ms by default) is abandoned
      one byte at a time. Codex named the right analogy — the removed `ReadExact` ladder could give
      up, and nothing replaced it. **This revives `ReadExactMaxRetries`, which the first pass had
      left dead**, with its original meaning, so the "option is silently ignored" wart below is
      gone. Its doc comment was updated again to describe the new use.
      Codex's framing that this "recreates the desynchronization this patch targets" overstates it:
      the scanner already self-resynced afterwards, so the damage was bounded to one bogus frame
      rather than persistent. The stall and the false completion are what justified the fix.
- [x] **P2 — `DroppedByteCount` was not published safely.** `long` is not atomically read on 32-bit
      runtimes and the getter took no barrier. Now `Interlocked.Add` / `Interlocked.Exchange` /
      `Interlocked.Read`. Note the obvious alternative is wrong: taking `_receiveLock` in the getter
      would block a diagnostic read behind `_port.Read`, which holds that lock for up to the read
      timeout. Recorded in the property's `<remarks>`.

Considered and dropped from the P1 fix: validating the declared payload length against the
definition's parameter list. It would deterministically reject corrupt-length headers, but variable
-length array parameters make the expected size a range rather than a value, and a wrong bound
would false-reject valid frames. The expiry bounds the same damage without that risk.

To make the P1 fix testable, `AppendReceived` and `TakeBufferedFrame` are now `internal` and
`ReadMessage` reads into a `_readChunk` staging array before appending. Both members sit on the real
receive path — there is no test-only API — at the cost of one extra memcpy of at most 4KB per poll,
negligible beside the serial I/O. That seam also closed the gap noted below.

Task 5's verify is now met: `DroppedByteCount_AdvancesByTheNumberOfBytesDropped` asserts the running
total, which the first pass had left untested because the counter was unreachable without a real
`SerialPort`.

The P1 fix was checked against its own defect the same way as the original: with the expiry
comparison neutered to `TimeSpan.MaxValue`,
`TakeBufferedFrame_StalledCandidate_IsAbandonedSoTheFollowingFrameIsFound` fails; restored, the
suite is green.

### Noticed, not touched (out of scope)

`BgapiHeader.CreateCommand` computes its message type as `CommandFlag | (deviceId << 3)`, and
`CommandFlag` is `0x20` — which is `4 << 3`. So the device-id nibble is OR-ed with 4, and any
device id that is not a bit-superset of 4 encodes wrong (id 3 becomes 7). Harmless today because
the only ids in play are bt=4 and btmesh=5, and `EventFlag` (`0xA0`) has the same shape. Left
alone; flagging it here rather than fixing it inside this spec.

### Review round (/code-review, 2026-09-02) — 12 of 15 findings taken

Full run after the round: **63 passed, 0 failed**, 0 build warnings.

Accepted, in the order they were fixed:

- [x] **Poison frame could wedge the receive path permanently.** `TryExtractFrame` decoded before
      `TakeBufferedFrame` consumed anything, so a decode that threw left the offending bytes at the
      front of the accumulator for the next poll to fail on identically — forever, with no resync
      and nothing counted. `TakeBufferedFrame` now catches, reports one dropped byte and rescans.
      No decode path throws today; the point is that the failure mode is unrecoverable, unlike
      every other bad-frame case.
- [x] **`AppendReceived` had no capacity check.** The 8192-byte margin rested on an unenforced
      invariant (`_rxCount <= 2050` at read time). An overflowing `CopyTo` throws *before* `_rxCount`
      advances, which is the same permanent wedge. Now drops the oldest bytes and reports them.
- [x] **`Open()` published `_port` outside `_receiveLock`.** A reconnect while the reader was
      blocked in `_port.Read` left a window in which the reader saw a new, not-yet-open port and
      threw on every 10ms iteration. The port is now opened first and published together with the
      stream reset, inside the lock. A failed open disposes its port instead of leaking it.
- [x] **`IsKnownHeader` bounded nothing but the triple.** Resolving `(device, class, index)` does
      not stop a mid-payload window that lands on a real command: its 11-bit length field claims up
      to 2047 bytes, and under continuous traffic those bytes *arrive* — swallowing real frames long
      before `StalledCandidateBudget` (500ms; 2000 bytes take ~174ms at 115200) could expire. The
      header must now also declare a payload the definition could produce. **This reverses the
      "considered and dropped" note in the round above.** That note's objection — variable-length
      arrays make the size a range — only rules out an exact match; an upper bound is well defined
      (`1 + 255` for a `uint8array`, `2 + 65535` for a `uint16array`, clamped to the 2047 the length
      field can express) and a parameter whose size cannot be derived disables the bound rather than
      risk a false reject. Most responses return only an errorcode, so the bound is 2 bytes.
      Confirmed with the user before reversing.
- [x] **`FindCommand` / `FindEvent` were three chained LINQ scans on the reader's hot path.**
      `IsKnownHeader` runs once per candidate byte, so a 4096-byte junk chunk cost ~300k comparisons
      and ~12k enumerator allocations per poll. `XapiDefinitions` now indexes
      `(deviceId, classIndex, index) -> (definition, max payload)` at load time; both finds are a
      dictionary lookup and the payload bound comes free with them.
- [x] **`ReadExactMaxRetries` was repurposed while keeping a name for deleted code.** It also
      coupled the abandon budget to the unrelated poll interval — raising `ReaderLoopReadTimeoutMs`
      from 100 to 1000 silently raised the budget from 500ms to 5s — and multiplied two `int`s, so
      large configured values wrapped negative. Replaced by `PartialFrameTimeoutMs` (500ms);
      the old option is `[Obsolete]` and no longer read. README updated.
- [x] **`BgapiDevice`'s `EventName is null` branch was unreachable.** The connector only emits
      frames `IsKnownHeader` accepted, and `DecodeMessage` resolves names with the identical
      lookups, so `EventName` is never null on the receive path — the diagnostic added for job 158
      could never have appeared in a Hub log. Task 6 above is therefore withdrawn: the branch and
      its test are gone (`~/.claude/CLAUDE.md` §2, no error handling for impossible scenarios).
      The nameable-mismatch test stays; framing desync is the connector's to report.
- [x] **Junk reporting was `LogWarning` on the per-poll receive path.** A boot banner or a floating
      line meant ~10 warnings/second indefinitely, burying the signal `DroppedByteCount` exists to
      give. Now one warning per resync episode (cleared when a good frame is delivered), the rest at
      Debug. The stalled-candidate call also passed a pre-interpolated `$"…"` as the structured
      `{Reason}` value, which made the field unqueryable; both reasons are constants now and the
      budget is logged once at `Open`.
- [x] **Nothing logged which four bytes were rejected.** That is the only thing distinguishing line
      noise from a frame the loaded XAPI simply does not define — the version-skew case, which the
      stricter `IsKnownHeader` turns from a harmless unnamed message into a resync. Added at Debug
      at the drop site. The accepted-header line stays deleted: `ReaderLoop` already logs the same
      fields for every message it delivers.
- [x] **`if (read <= 0) return null;`** was unreachable (`SerialPort.Read` never returns 0 for a
      non-zero count) and inconsistent with the `TimeoutException` path two lines above, which
      deliberately drains the accumulator. Deleted.
- [x] Tests: the payload bound, a fabricated length not swallowing the following frame, and the
      accumulator overflow are covered. `TruncatedFrame`/the split-frame test moved onto
      `mbt_client.get_status` (returns 7 bytes) because `query_block_status` returns an errorcode
      and nothing else — under the new bound a 8-byte payload on it is correctly impossible.

Rejected, with reasons:

- **"`knownDeviceIds` is redundant with `IsKnownHeader`."** True for the default path, where
  `BgapiDevice` passes exactly the device ids of the same definitions the protocol queries. But
  `SetKnownDeviceIds` is public NuGet API and a consumer may pass a *narrower* set — load bt and
  btmesh, accept only bt. Removing the parameter breaks that contract to save one hash lookup.
  `TryExtractFrame_UnknownDeviceIdInPreFilter_IsSkipped` covers that contract, not an unproducible
  state.
- **"The stalled-candidate clock restarts per byte instead of per corrupt region."** Real, but the
  region-scoped fix has a worse failure: carrying the clock across a drop destroys a legitimate
  partial frame sitting at the tail of the buffer immediately after the region, one byte at a time.
  The cost it would buy is now negligible anyway — with the length bound in place, a mid-payload
  window that both resolves *and* declares a producible length is rare. Left per candidate, with
  the reasoning in the code.
- **"No test exercises `ReadMessage` itself."** The framing logic all moved into
  `TryExtractFrame`/`TakeBufferedFrame`, which are covered; what is left in `ReadMessage` is the
  port read and the accumulator hand-off. The accumulator contract is now tested directly
  (overflow, buffered drain, partial retention). Adding an `ISerialStream` seam to the public
  connector for the remaining sliver is a larger change than the gap justifies.
