# AxetosOS Products / NES v2.3.0

## True-hardware package-boundary batching

v2.3.0 optimizes the generic package boundary without changing physical semantics or adding NES-specific fused behavior.

- Stages changed output pins 0-63 as one package-owned `ulong` mask, eliminating the normal per-transition changed-pin reference append and per-pin publication-sequence check.
- Retains a reusable overflow list for generic laboratory packages with physical pins beyond index 63.
- Publishes every changed physical package pin and resolves every attached motherboard net exactly as before.
- Accumulates destination changed-input masks in the reusable propagation frame by component index during an atomic multi-output publication.
- Enters each destination package once with its combined physical pin-change mask after all source-package traces have been presented.
- Preserves re-entrant behavior: a destination package which is already executing owns newly arriving changes in its package-local pending mask and consumes them before returning.
- Adds a 16-line fan-out conformance test requiring every input pin to receive the new level while the receiver activates once with mask `0xFFFF`.

Validated v2.2.0 baseline supplied by the user before this patch:
- tests: 226/226
- Super Mario Bros. reference/uncapped: 21.30 FPS average
- Donkey Kong reference/uncapped: 19.16 FPS average

Expected test count after applying v2.3.0: **228**.

This remains generic hardware-lab infrastructure: no chip learns what peer chip or wire is attached, no board signal meaning is introduced, and no physical pin/net transition is bypassed.
