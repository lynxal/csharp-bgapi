# Embed Default XAPI Definitions as Assembly Resources

## Context

The SilabsBgapi library currently requires users to source XAPI definition files (sl_bt.xapi, sl_btmesh.xapi) from their Gecko SDK installation and load them explicitly. This creates friction for new users. By embedding these files as assembly resources, users can call `device.LoadDefaultXapis()` to get started immediately, while still being able to provide custom XAPI files via the existing `LoadXapi(path)` method.

## Changes

1. Copy sl_bt.xapi and sl_btmesh.xapi into `src/SilabsBgapi/Xapi/`
2. Embed as assembly resources via .csproj `<EmbeddedResource>` directive
3. Create `EmbeddedXapiResources.cs` — internal helper centralizing resource names
4. Add `BgapiDevice.LoadDefaultXapis()` public method
5. Add `XapiDefinitions.HasDefinitions` and `LoadedApiNames` introspection properties
6. Update DI extension with `loadDefaultXapis` parameter
7. Update example, README, and CHANGELOG

## Key Files

- `src/SilabsBgapi/Xapi/sl_bt.xapi` — Bluetooth Core API (device_id=4, v10.1.1)
- `src/SilabsBgapi/Xapi/sl_btmesh.xapi` — Bluetooth Mesh API (device_id=5, v10.1.1)
- `src/SilabsBgapi/Protocol/EmbeddedXapiResources.cs` — Resource access helper
- `src/SilabsBgapi/BgapiDevice.cs` — LoadDefaultXapis() method
- `src/SilabsBgapi/Protocol/XapiDefinitions.cs` — HasDefinitions, LoadedApiNames
- `src/SilabsBgapi/SilabsBgapiServiceExtensions.cs` — DI loadDefaultXapis parameter
