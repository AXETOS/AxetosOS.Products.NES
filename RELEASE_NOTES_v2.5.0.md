# AxetosOS Products / NES v2.5.0

## Aggressive true-hardware package/electrical sweep

- Built from the validated v2.2 true-hardware runtime; v2.3/v2.4 performance experiments are intentionally rolled back.
- Replaces per-reaction changed-output reference arrays and publication-sequence stamps with a 64-bit physical package-pin change mask.
- Retains a generic overflow path for laboratory packages with more than 64 pins.
- Adds package-local parallel 6/8/16-bit `DigitalBus` drive/release staging when the bus belongs to one reacting package.
- Packs each output pin's drive level and strength into one byte and resolves shared nets directly from that compact electrical state.
- Uses byte-sized `DigitalLevel`, `DigitalDriveStrength` and `PinDirection` enum storage.
- Preserves individual pin state changes, independent per-net resolution, Hi-Z, weak/strong priority, unknown, contention, bidirectional behavior and package-boundary atomicity.
- Adds conformance for an atomic eight-pin physical bus change-set and repeated same-pin changes publishing only the final package drive state.

Expected test count: **228**.

There is no NES/Famicom/NROM/CPU/PPU routing knowledge in these optimizations. Chips still know only their own pins/internal circuitry and boards still define only physical topology.
