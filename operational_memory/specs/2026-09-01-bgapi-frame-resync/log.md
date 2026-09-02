# Status log

<!-- Append-only ledger of THIS spec's status transitions. One line per
     transition, newest last:
     - YYYY-MM-DD HH:MM — <status> · <author>
     The closer memos:promote writes on its CM copy carries one more field,
     the source commit the copy was taken at — how a later pass tells an
     untouched spec from one edited since it was promoted:
     - YYYY-MM-DD HH:MM — archived · <author> · from <sha>
     Per-spec on purpose (D33): two branches shaping or advancing two specs
     touch two different files, so status never merge-conflicts. Never edit
     past lines. Seeded by memos:shape-spec / memos:promote / memos:archive
     from this template. -->
- 2026-09-01 19:21 — active · Sipan
