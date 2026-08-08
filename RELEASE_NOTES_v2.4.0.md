# AxetosOS Products / NES v2.4.0

## True-hardware discrete-chip retained input stages

v2.4.0 backs out the v2.3 package-boundary aggregation experiment after it benchmarked slower than the validated v2.2 physical runtime. The electrical transport and package-boundary core are restored to v2.2 behavior, and the optimization moves inside the reusable discrete IC packages.

### Changes

- HM6116 keeps chip-local retained address/data input-buffer state while its own read/write stages are connected. Address/data-only reactions fold only the physically changed pins into that state instead of rescanning 11 address plus 8 data pins.
- HM6116 synchronizes the complete current package-pin levels whenever control/power reconnects a sleeping address/data stage, so wake suppression never hides a rewired or externally changed pin level.
- SN74LS373 keeps an eight-bit retained D input bank while LE is transparent and updates only changed D bits. LE reconnect performs one complete physical package sample before the latch becomes transparent.
- SN74LS139A retains each section's output state and changes only the active-low decoder outputs that need to switch during ordinary known A/B selection changes.
- SN74LS368A converts its contiguous input-change mask directly into six channel bits and uses fixed per-channel reactions rather than rescanning group arrays.
- Restores v2.2 `DigitalNet`, `DigitalPin`, `VirtualHardwareComponent`, and chip-boundary test behavior, removing v2.3 aggregation overhead.
- Adds conformance tests for LS373 and HM6116 reconnecting from physical pin levels after their internal data/address wake stages slept.

Expected test count after applying over v2.3.0: **228** (the two v2.3 aggregation tests are removed and two chip reconnect tests are added).

This release contains no board-aware chip logic and no NES-specific shortcut in the true-hardware runtime.
