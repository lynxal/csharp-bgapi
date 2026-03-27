# References for Embed Default XAPI Definitions

## Similar Implementations

### residential-big SilabsBgapi (private)

- **Location:** `residential-big/project-interconnected/libs/SilabsBgapi/`
- **Relevance:** Original project where XAPI files were bundled as content files
- **Key patterns:** Uses `<Content Include="Xapi\*.xapi">` with `PackageCopyToOutput=true` and loads from `AppContext.BaseDirectory/Xapi/` at runtime
- **Why we diverged:** Embedded resources are simpler for NuGet distribution — no filesystem path assumptions

### NcpLifecycleService (private)

- **Location:** `residential-big/project-interconnected/src/CanvasHub.Infrastructure/Mesh/NcpLifecycleService.cs`
- **Relevance:** Shows production usage pattern — `LoadXapi(Path.Combine(xapiDir, "sl_bt.xapi"))` called during service initialization
- **Key patterns:** XAPI files loaded before device.Open(), error handling with logging
