# AxetosOS Products / NES v2.44.0

## Mapper 16 / Bandai FCG

v2.44.0 adds Mapper 16 as replaceable physical cartridge hardware.

Implemented board distinctions:

- NES 2.0 submapper 4 — Bandai FCG-1/2;
- NES 2.0 submapper 5 — Bandai LZ93D50 with no EEPROM or a 256-byte 24C02 serial EEPROM;
- submapper 0 / legacy compatibility — both documented FCG and LZ register windows remain visible with their corresponding IRQ programming semantics.

The cartridge package provides:

- one switchable 16 KiB PRG-ROM bank at `$8000-$BFFF`;
- the final 16 KiB PRG bank fixed at `$C000-$FFFF`;
- eight independently switchable 1 KiB CHR-ROM windows;
- vertical, horizontal and both single-screen CIRAM routes;
- a 16-bit CPU-cycle IRQ counter exposed through the physical cartridge IRQ pin;
- FCG-1/2 direct counter programming;
- LZ93D50 IRQ reload-latch behavior;
- optional board-local 256-byte 24C02 serial EEPROM using the ASIC's SDA/SCL control register and CPU D4 readback path.

Deprecated mapper-16 submappers 1, 2 and 3 are rejected rather than approximated; their distinct board hardware belongs to mapper 159, 157 and 153 respectively.

No NES/mapper-specific logic was added to the generic hardware compiler or motherboard.

## Validation

The previously validated v2.43.0 baseline is 514 Release tests. v2.44.0 adds **28 test cases**, taking the expected Release suite to **542 tests**, covering banking, register-window decode, all four mirroring modes, both IRQ circuit variants, physical M2 IRQ clocks, compiled/raw parity, 24C02 serial transactions, legacy battery metadata and factory composition.

A synthetic Mapper-16 LZ93D50 sample ROM is included at:

`samples/axetos-bandai-fcg-irq.nes`

## README cleanup

The main README has been rewritten around architecture, supported hardware, usage and development principles. Historical per-release FPS/headroom measurements were removed from the README. Runtime diagnostics and release-specific investigation data remain available where useful without turning the project overview into a benchmark log.
