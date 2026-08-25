# Version compatibility

## v0.111.0

- Compiled against the installed retail assemblies.
- Runtime smoke test passed on 2026-08-25: the main-menu hook, every top-level page, every team-size lobby, and the complete 4v4 demo queue flow rendered with no pre-shutdown Godot errors.

## v0.107.1

- The same DLL targets the public API intersection shared with v0.111.0.
- `tools/check-api-compat.ps1` validates every referenced main-menu hook and original UI resource against both extracted source trees.
- The mod deliberately avoids the Steam invite API that changed between these versions.
- The available v0.107.1 workspace contains extracted source rather than a retail executable, so an actual v0.107.1 runtime smoke test remains a release gate and is not claimed by source compatibility alone.
