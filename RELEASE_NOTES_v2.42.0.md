# AxetosOS Products / NES v2.42.0

## Mapper 9 / Nintendo MMC2 / PxROM physical cartridge

- Adds Mapper 9 as a distinct replaceable MMC2/PxROM cartridge package.
- Models CPU PRG wiring as one switchable 8 KiB window at `$8000-$9FFF` followed by the final three fixed 8 KiB banks.
- Supports power-of-two fitted PRG populations through 128 KiB, with the physical low four PRG-bank outputs masked only by fitted ROM address lines.
- Models four 5-bit CHR bank registers: FD/FE alternatives for each 4 KiB PPU pattern-table window, over fitted CHR ROM through 128 KiB.
- Models the two MMC2 tile latches as PPU-address-driven state:
  - exact `$0FD8` selects low-window FD;
  - exact `$0FE8` selects low-window FE;
  - `$1FD8-$1FDF` selects high-window FD;
  - `$1FE8-$1FEF` selects high-window FE.
- The triggering PPU read is completed from the previously selected CHR bank before its decoded latch transition affects following reads.
- Adds mapper-controlled horizontal/vertical CIRAM A10 routing through the cartridge's live output; this mutable route is intentionally not statically folded by the generic compiler.
- Models PxROM as CHR-ROM-only, with no PRG RAM, CHR RAM, IRQ or CPU/ROM bus conflicts. Legacy iNES inferred PRG-RAM metadata is not converted into physical PxROM RAM.
- Adds compiled/raw physical parity coverage that performs the MMC2 register program and the four PPU trigger reads through the complete CPU/PPU hardware paths.
- Adds focused conformance coverage for fitted PRG/CHR address lines, exact MMC2-vs-MMC4 low-latch decoding, trigger-read ordering, live mirroring and unsupported hardware variants.
- Adds `samples/axetos-mmc2-tile-latch.nes`, a 128 KiB PRG + 128 KiB CHR Mapper-9 smoke cartridge that programs all four CHR registers and drives all four latch transitions through `$2007`.
- Adds desktop shutdown diagnostics for MMC2 registers, live latch selection and per-trigger counts.
- No generic whole-circuit compiler or motherboard mapper-specific behavior is added.
- Expected Release suite: 494 tests, pending local validation.
