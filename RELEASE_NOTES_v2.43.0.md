# AxetosOS Products / NES v2.43.0

## Mapper 10 / Nintendo MMC4 / FxROM physical cartridge

- Adds `Mmc4Cartridge` as a replaceable physical Mapper-10 package rather than treating MMC4 as MMC2 with altered constants.
- Models one switchable 16 KiB PRG-ROM bank at `$8000-$BFFF` and the final 16 KiB PRG-ROM bank fixed at `$C000-$FFFF`, with up to 256 KiB fitted PRG ROM.
- Models the FxROM 8 KiB PRG RAM/NVRAM window at `$6000-$7FFF`, including raw physical and compiled bus access.
- Models four 5-bit CHR bank registers and two independent 4 KiB FD/FE-selected CHR-ROM windows, up to 128 KiB CHR ROM.
- Models the MMC4 PPU-address latch ranges `$0FD8-$0FDF`, `$0FE8-$0FEF`, `$1FD8-$1FDF`, and `$1FE8-$1FEF`.
- Preserves latch timing: the triggering CHR read returns data from the previously selected bank and the new FD/FE state applies to subsequent accesses.
- Models live mapper-controlled horizontal/vertical CIRAM routing.
- Correctly provides no IRQ output, no CHR RAM and no CPU/ROM bus conflicts.
- Adds Mapper-10 factory/catalog/desktop diagnostics and `hardware/boards/mmc4.json`.
- Adds `samples/axetos-mmc4-tile-latch.nes`, which exercises PRG RAM, PRG banking, all four CHR registers, all four PPU latch classes and mapper-controlled mirroring.
- Adds 20 Mapper-10 conformance tests covering fitted address lines, latch decode/timing, raw physical PPU behavior, PRG RAM, compiled/raw parity, mirroring, invalid board geometry and factory composition.
- No generic compiler or motherboard semantics are changed.
- Expected Release suite: **514 tests**, pending local validation.
