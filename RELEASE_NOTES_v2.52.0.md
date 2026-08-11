# AxetosOS Products / NES v2.52.0

Validated baseline entering this release: **v2.51.3 — 761 / 761 Release tests passing**, with MMC5/Castlevania III visually clean after the CHR fetch-geometry fix.

## Whole-machine save-state contracts

`VirtualNesBootHost` now exposes two application-neutral state paths.

`CaptureState` / `RestoreState` retain the fast opaque in-memory checkpoint used by a running host for F5/F7-style quick save/load. The state is bound to the exact `VirtualNesBootHost` and loaded-cartridge generation that created it and may be restored repeatedly while that cartridge remains loaded.

`CapturePortableState` / `RestorePortableState` add a versioned cross-process payload for persistent save files. The payload contains no live object references and no ROM bytes. A host reloads and compiles the exact cartridge image, verifies its own ROM identity metadata, then applies the portable hardware state to the matching motherboard/mapper.

Both contracts capture mutable physical-machine state rather than CPU/PPU/mapper shortcuts: chip internals, RAM, cartridge/mapper registers and RAM, package pin/net state, clock state, retained package-output state and compiled-runtime mutable state are traversed from the assembled machine. Immutable ROM data and the host's current external controller-button contacts stay outside the snapshot.

Portable restoration validates the complete captured hardware-member schema before state is applied, and dynamically sized value buffers can be recreated when the freshly compiled machine starts with a different buffer capacity.

This remains a hardware-engine responsibility only. Save-file naming, ROM SHA-256 identity, storage paths, dialogs, thumbnails and application hotkeys belong to the consuming host and are not part of the public NES hardware engine.

## Conformance coverage

Seven boot-host tests cover the save-state contracts:

- restore the exact in-memory hardware point and deterministically replay the same future master cycles;
- deterministically replay the compiled MMC1 runtime after in-memory restore;
- keep current external controller-button contacts outside the rewind;
- reject attempts to restore an in-memory state into a different host instance;
- restore a portable state into a newly assembled host and deterministically replay the same future execution;
- prove that portable payloads do not embed cartridge PRG/CHR ROM byte sequences;
- reject a portable state when the newly loaded mapper does not match.

The resulting test suite must be validated locally before this candidate is considered the new public baseline.
