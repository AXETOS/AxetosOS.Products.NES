# AxetosOS Products / NES v2.30.0

## Mapper 2 / UxROM physical cartridge hardware

- Adds `UxromCartridge` as a replaceable Mapper-2 cartridge package.
- Implements a 16 KiB switchable lower PRG window and fixed-last 16 KiB upper PRG window.
- Implements the cartridge-local bank latch at CPU bus-cycle completion.
- Implements 8 KiB CHR RAM with physical PPU read/write behavior and generic compiled bus facets.
- Keeps nametable mirroring as fixed cartridge CIRAM wiring and leaves IRQ high-impedance.
- Supports NES 2.0 Mapper-2 submapper 1 (no bus conflicts) and submapper 2 (bus conflicts); legacy/unspecified Mapper 2 uses classic conflict-capable behavior.
- Rejects Mapper-2 hardware combinations that require a different physical board, including CHR ROM, four-screen nametable RAM, explicit PRG RAM/NVRAM, and non-8-KiB CHR RAM.
- Adds UxROM runtime diagnostics to the desktop host.

## Conformance coverage

- Adds direct PRG bank/fixed-bank tests.
- Adds CHR-RAM round-trip tests.
- Adds fixed mirroring/static-combinational tests.
- Adds bus-conflict submapper tests.
- Adds a physical falling-M2 bank-latch test.
- Adds generic-compiled versus raw-physical execution parity for a bank-switching Mapper-2 program.
- Updates ROM-loading/factory coverage so Mapper 2 is supported and Mapper 3 remains the next unsupported cartridge hardware.

The generic compiler and motherboard contain no Mapper-2/UxROM-specific dispatch or address-map knowledge.
