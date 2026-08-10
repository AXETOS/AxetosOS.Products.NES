# AxetosOS Products / NES v2.40.0

## Mapper 206 / DxROM / Namco 108 / MIMIC-1

This release adds Mapper 206 as its own replaceable physical cartridge package rather than routing it through MMC3 behavior.

Implemented hardware behavior:

- two switchable 8 KiB PRG-ROM windows at `$8000-$9FFF` and `$A000-$BFFF`;
- the final two 8 KiB PRG-ROM banks fixed at `$C000-$DFFF` and `$E000-$FFFF`;
- two 2 KiB CHR-ROM windows followed by four 1 KiB CHR-ROM windows;
- bank-select decode only from the low three bits written to even `$8000-$9FFE` addresses;
- bank-data latch using only the low six mapper outputs written to odd `$8001-$9FFF` addresses;
- fixed horizontal/vertical CIRAM wiring;
- DRROM four-screen nametable RAM using an 8 KiB cartridge SRAM with console CIRAM disabled;
- NES 2.0 submapper 1 support for 3407/3417/3451 boards whose 32 KiB PRG ROM is wired directly to CPU A13/A14 and therefore does not bank;
- optional 8 KiB PRG RAM only when the image explicitly describes the known MIMIC-1 prototype-style exception (or a legacy battery-backed image requires it);
- no IRQ output, mapper-controlled mirroring, MMC3 PRG mode, MMC3 CHR inversion, or standard MMC3 PRG-RAM protection register.

The generic whole-circuit compiler and NES motherboard contain no Mapper-206 checks or shortcuts. The cartridge exposes the same product-agnostic compiled bus/combinational facets used by the other replaceable cartridge packages.

## Validation

- Previous validated baseline: v2.39.0, **436 / 436 tests**.
- New Mapper-206 coverage: **16 test cases**.
- Expected v2.40.0 Release suite: **452 tests**, pending local execution.
- Added `samples/axetos-dxrom-bank-switch.nes`, a synthetic 128 KiB PRG / 64 KiB CHR Mapper-206 cartridge that writes R6=1, R7=2 and R0=4 while executing continuously from the fixed last PRG bank.
