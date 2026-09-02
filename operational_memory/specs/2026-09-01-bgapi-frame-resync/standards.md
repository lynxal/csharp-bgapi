# BGAPI serial frame resync — applicable standards

<!-- Pulled at shaping by memos:shape-spec (precedence: project overrides
     product by category/slug). Reference standards as `category/slug`; the
     effective text comes from the CM at recall — never copy it here.
     Checked = applies to this spec and is binding on the implementation.
     Unchecked = considered and deliberately dropped; keep the reason so the
     call is not re-litigated next session.

     IMPLEMENTING THIS SPEC — in the shaping session or any later one — starts
     here, before the first code change: render each checked ref's effective
     view and work to it, and stop rather than proceed on one that fails to
     resolve.
       <python> <skills-dir>/memos-index/scripts/effective_standard.py \
         --root <CM_ROOT> --project silabs-bgapi-csharp --standard <category>/<slug>
     -->

- [x] `error-handling/decode-tolerates-malformed-input` — the whole change is inbound frame
      handling; the extractor must degrade to "no frame this poll" and never throw on garbage.
- [x] `error-handling/fail-open-reader-and-dispatch` — the accumulator sits on the reader
      thread's path; a malformed frame must not kill the loop, and the fail-open catch stays.
- [x] `error-handling/reject-stale-command-responses` — this spec splits the desync case out of
      that drop; the ratified identity check itself must keep working unchanged.
- [x] `protocol-codec/binary-wire-format` — the extractor parses the 4-byte bit-packed header
      and its payload length; it must read them exactly as this standard specifies.
- [x] `protocol-codec/xapi-definition-driven` — header validation goes through the XAPI lookups
      (`FindCommand`/`FindEvent`), not a hand-written table of valid class indices.
- [x] `testing/stack-and-shape` — one new `BgapiConnectorFrameTests` class, all `[Fact]`,
      FluentAssertions, reaching the extractor via the existing `InternalsVisibleTo`.
- [x] `testing/regression-tests-document-the-defect` — the new test class opens with the
      incident: block 192, `dev=5 cls=0 idx=0`, and the old discard-on-short-read behaviour.

- [ ] `resilience/retry-until-transient-status` — considered and deliberately not changed.
      Making `SlStatus.Timeout` retryable in the library default would contradict this ratified
      standard; the fix lands at Hub's call site instead (see `references.md`).
- [ ] `error-handling/command-status-vs-typed-exceptions` — no new status value or exception type
      is introduced; `ReadMessage` keeps returning `null` rather than throwing.
- [ ] `api-design/dual-construction-di-and-plain` — no new public type; the extractor is
      `internal static` on the existing connector, so there is no constructor pair to provide.
