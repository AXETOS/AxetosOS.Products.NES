# AxetosOS Products / NES v2.31.0

## Mapper 3 / CNROM physical cartridge hardware

- Adds `CnromCartridge` as a replaceable Mapper-3 cartridge package.
- Implements one fixed 32 KiB PRG-ROM window and an end-of-M2 CHR-bank latch.
- Implements a switchable 8 KiB CHR-ROM window with ROM-capacity-derived bank-address wiring.
- Keeps nametable mirroring as fixed cartridge CIRAM wiring and leaves IRQ high-impedance.
- Supports NES 2.0 Mapper-3 submapper 1 (no bus conflicts) and submapper 2 (AND bus conflicts); legacy/unspecified Mapper 3 remains conflict-capable.
- Rejects cartridge metadata that requires hardware outside the standard CNROM profile, including four-screen nametable RAM, cartridge RAM/NVRAM, battery-backed memory, non-32-KiB PRG ROM, and missing/oversized CHR ROM.
- Adds CNROM runtime diagnostics to the desktop host.

## Conformance coverage

- Adds fixed-PRG and CHR-bank switching tests.
- Adds connected-bank-line masking tests.
- Adds fixed mirroring/static-combinational tests.
- Adds bus-conflict submapper tests.
- Adds a physical falling-M2 bank-latch test.
- Adds read-only CHR-ROM compiled-bus coverage.
- Adds generic-compiled versus raw-physical execution parity for a Mapper-3 bank-switching program.
- Updates ROM-loading/factory coverage so Mapper 3 is supported and Mapper 4 / MMC3 becomes the next unsupported cartridge hardware.
- Adds `samples/axetos-cnrom-bank-switch.nes` for desktop-host smoke testing.

No generic compiler or motherboard code contains Mapper-3/CNROM-specific dispatch or address-map knowledge.
