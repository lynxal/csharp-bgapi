# Embed Default XAPI Definitions — Shaping Notes

## Scope

Add built-in XAPI definitions (Bluetooth and Bluetooth Mesh) as embedded assembly resources so library users can load protocol definitions without sourcing files from the Gecko SDK.

## Decisions

- **Embedded resources** chosen over content files — avoids filesystem dependencies, simpler NuGet packaging
- **Explicit loading** via `LoadDefaultXapis()` — no auto-loading; users opt in
- **DI parameter** (`loadDefaultXapis: true`) for DI users who want auto-loading at registration
- **No-hardcoding** — resource name strings centralized in `EmbeddedXapiResources.cs`
- **Existing API preserved** — `LoadXapi(path)` and `LoadXapiFromStream(stream)` remain for custom files

## Context

- **Visuals:** None
- **References:** Private project residential-big bundles XAPI as content files with PackageCopyToOutput; this approach uses embedded resources instead for cleaner distribution
- **Product alignment:** Reduces friction for new users of the open-source library

## Standards Applied

- no-hardcoding — Resource names centralized, not scattered as magic strings
- mesh/xapi-parameter-parity — Bundled XAPI files define the parameter contract for correct command marshalling
