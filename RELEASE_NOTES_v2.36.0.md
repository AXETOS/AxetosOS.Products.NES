# AxetosOS Products / NES v2.36.0

## Mapper 66 / GxROM physical cartridge

v2.36.0 adds Mapper 66 / GxROM as another replaceable cartridge circuit without adding mapper semantics to the motherboard or generic hardware compiler.

The cartridge models the standard GNROM/MHROM discrete-logic arrangement:

- one switchable 32 KiB PRG-ROM window;
- one switchable 8 KiB CHR-ROM window;
- a four-bit 74HC161-style latch clocked at the end of the qualified CPU write window;
- CPU D4-D5 wired to PRG bank address lines;
- CPU D0-D1 wired to CHR bank address lines;
- up to four standard PRG banks and four standard CHR banks, with absent fitted-ROM address lines remaining physically absent rather than modulo-normalized;
- fixed horizontal/vertical CIRAM wiring;
- no PRG RAM, CHR RAM, IRQ or expansion audio;
- standard AND-style CPU/PRG-ROM bus conflicts.

Mapper metadata is interpreted only by `VirtualCartridgeHardwareFactory` to select the physical cartridge package. Compiled execution then sees the same generic bus-target/combinational facets used by the other replaceable cartridges.

## Validation coverage

The patch adds direct banking, fitted-ROM wiring, bus-conflict, mirroring, physical falling-M2 latch, compiled write-phase, CHR-ROM read-only, raw-vs-generic-compiled parity, invalid-geometry and factory coverage.

The previous validated hardware baseline remains v2.34.1 at **359 / 359 tests**. v2.35.0's native loading screen was confirmed in local desktop use and did not add hardware tests. v2.36.0 adds 13 Mapper-66 test cases, so the expected Release suite is **372 tests** pending local validation.

A deterministic `samples/axetos-gxrom-bank-switch.nes` smoke cartridge is included. Its safe bus-conflict write selects PRG bank 2 and CHR bank 1, producing a final GxROM register value of `$21`.
