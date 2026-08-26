# Version compatibility

## v0.111.0

- Compiled against the retail assemblies in `gamests2 111.0`.
- Runtime smoke test passed on 2026-08-25: the main-menu hook, every top-level page, every team-size lobby, and the complete 4v4 demo queue flow rendered with no pre-shutdown Godot errors.

## v0.107.1

- The same DLL targets the public API intersection shared with v0.111.0.
- Compiled successfully against the current Steam v0.107.1 retail assemblies.
- `tools/check-api-compat.ps1` validates every referenced main-menu hook and original UI resource against both extracted source trees.
- Steam host/client services and `JoinFlow` are constructed through a version adapter because their constructors changed in v0.111.0.
- A full two-account v0.107.1 runtime match remains a release gate and is not claimed by compilation alone.
