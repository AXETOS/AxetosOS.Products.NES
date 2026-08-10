# AxetosOS Products / NES v2.32.0

## Mapper 4 / MMC3-family physical cartridge hardware

v2.32.0 adds Mapper 4 as a replaceable cartridge package rather than a motherboard or compiler special case.

Implemented package-owned behavior includes:

- four 8 KiB CPU PRG windows with MMC3 PRG mode inversion;
- two 2 KiB plus four 1 KiB CHR bank registers with CHR inversion;
- switchable horizontal/vertical CIRAM wiring;
- 8 KiB MMC3 PRG RAM enable/write protection;
- NES 2.0 submapper 1 MMC6 1 KiB internal RAM control;
- submapper 2 hard-wired mirroring;
- submapper 4 NEC/old IRQ-zero behavior;
- CHR-RAM Mapper-4 board support;
- four-screen cartridge nametable SRAM (8 KiB fitted, 4 KiB nametable address space decoded);
- filtered PPU-A12 IRQ counter, reload, enable, disable and acknowledge;
- open-drain/high-impedance cartridge IRQ behavior;
- mapper/IRQ runtime diagnostics;
- synthetic Mapper-4 bank-switch smoke ROM.

The generic whole-circuit compiler gains one product-agnostic capability: a compiled bus descriptor may observe a read address/control transaction without driving the data bus. This is required by arbitrary edge-sensitive external hardware and contains no NES/MMC3 knowledge.

MC-ACC (NES 2.0 submapper 3) and T9552 scrambling hardware (submapper 5) are deliberately rejected because they are distinct external circuits rather than aliases for Nintendo MMC3 silicon.

No motherboard code contains mapper semantics.
