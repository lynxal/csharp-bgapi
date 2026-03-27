# Standards for Embed Default XAPI Definitions

The following standards apply to this work.

---

## no-hardcoding

All tunable values (timeouts, sizes, indices, thresholds) must come from configuration (appsettings.json + Options pattern). Only true protocol constants (e.g., header sizes, opcode lengths, fixed byte array sizes) may remain as const.

### Application to this feature

Embedded resource name strings (`SilabsBgapi.Xapi.sl_bt.xapi`, `SilabsBgapi.Xapi.sl_btmesh.xapi`) are centralized in `EmbeddedXapiResources.AllResourceNames` rather than scattered across the codebase.

---

## mesh/xapi-parameter-parity

Every SendCommand call for btmesh classes must include all parameters defined in sl_btmesh.xapi, in the same order, with correct types.

### Application to this feature

The bundled sl_btmesh.xapi file IS the parameter contract. By embedding it in the assembly, we ensure the contract is always available and version-matched with the library code.
