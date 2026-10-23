# Release validation

The driver uses a byte-stream emulator with the production connector, pending read,
decoder, reader loop, command serialization and event queue. It never opens a serial
port. Internal seams preserve the existing public constructors and OS adapter.

```sh
dotnet build CsharpBgapi.slnx -c Release -p:Version=1.0.0-ci.44
dotnet test CsharpBgapi.slnx -c Release --no-build
dotnet run --project examples/ReleaseValidation -f net10.0 -c Release --no-build -- --output artifacts/validation/performance.json --count 4000 --repetitions 3 --seed 20261023 --rate 2000
python scripts/package-smoke.py --version 1.0.0-ci.44 --baseline-version 0.3.0
```

Use the actual git-derived candidate version for both build and pack. The package
script pins a stable SDK in its temporary workspace (`--sdk` overrides 10.0.402),
restores into an empty cache, keeps vulnerability audit and warnings-as-errors on,
then builds, tests, packs and runs direct, transitive and ProjectReference consumers.
`--skip-build` is only for a version already built and tested by CI. It never pushes
a package. Raw logs, packages and generated consumers stay under `artifacts/validation`.

Measurements record throughput, latency percentiles, process-wide allocation, GC,
CPU, working set, managed memory, counts and drops. CPU and allocations include the
emulator, pacing and measurement code. Logging uses a formatting sink, not disk I/O.
Four producers and four disjoint waiters retain per-producer order; subscriptions
are counted separately. Only overload/subscriber-only runs expect queue evictions;
only the fault workload expects framing discards. Both use a 32-event queue:
subscriber-only has no waiters and evicts exactly `N-32`, while overload keeps the
four waiters claiming in order at 100 µs per claim, so it checks that every event
is claimed once or evicted and that drops are reported, not an exact count. Burst
input sends 64-event batches per producer separated by a 20 ms idle gap, longer
than the 10 ms reader poll. These configured synthetic loads
are not an approved production workload or a Raspberry Pi performance budget.

The released-baseline comparison compiles `scripts/PublicMeasurements.cs` unchanged
against each actual nupkg. It compares the common public codec, queue and subscription
registration paths. It cannot compare the old binary through new internal seams.

Tests tagged `KnownLimitation` **confirm failures**, not release acceptance. They
currently reproduce inline callback blocking only: a slow subscriber, or one that
sends a command synchronously, holds the only reader. Same-header response
ambiguity, concurrent opens, old-reader dispatch after reopen, dispose during a
pending command and the unobserved late read fault after final close were repaired;
their reproductions now assert the repaired contract.
Passing these tests does not authorize release. Repair or explicit accepted operating
restrictions, performance budgets, Raspberry Pi evidence and the existing soak gate
are still required.

`scripts/HardwareObservation.cs` is an opt-in public-API observer to compile against
the candidate nupkg on Debian. Its required arguments record the user's port, baud,
handshake, queue capacity, duration and timeout. It never sends commands unless
`--command api.class.command` is provided. It cannot prove wire loss without sender
counts and cannot replace representative application/firmware operations.

The full local acceptance matrix and hardware procedure are in
`operational_memory/specs/2026-10-23-release-validation/` (local Operational Memory).
