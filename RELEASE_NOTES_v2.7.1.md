# AxetosOS Products / NES v2.7.1

## Selective RP2A0x core sweep

v2.7.1 rebuilds the RP2A03/RP2A07 optimization from the validated v2.6.0 baseline and deliberately removes the v2.7.0 package-output mirror experiment.

### Retained from v2.7.0

- byte-sized CPU cycle, interrupt, operation and addressing-mode retained state;
- 256-entry chip-local opcode PLA with 193 fixed operation/addressing-mode plans;
- compact store/read-modify-write operation masks;
- direct status-latch updates for BIT, shifts, rotates, ADC, compare and Z/N updates;
- RP2A03 dominant physical master-clock fast path;
- early rejection of writes outside the integrated $4000-$4017 APU/I/O register range;
- packed five-channel APU mixer-change state.

### Removed from v2.7.0

The retained mirrors for A0-A15, D0-D7, R/W and controller /OE outputs are removed. The chip again drives/releases its real package buses directly on each CPU bus phase, using the generic package/electrical layer to suppress unchanged physical outputs. Controller wake and sampling state again derives from the actual package /OE pin drive levels.

This preserves the hardware-lab rule: RP2A03/RP2A07 know only themselves and their own pins/internal circuitry. No motherboard, RAM, PPU, cartridge, mapper or wiring semantics are introduced.

Expected test suite: **228 tests**. Runtime performance must be validated locally.
