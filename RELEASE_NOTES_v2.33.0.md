# AxetosOS Products / NES v2.33.0

## Mapper 7 / AxROM physical cartridge hardware

v2.33.0 adds Mapper 7 without adding Mapper-7 knowledge to the motherboard or generic compiler.

The new `AxromCartridge` owns the physical cartridge behavior:

- one switchable 32 KiB CPU PRG-ROM window at `$8000-$FFFF`;
- up to 256 KiB of PRG ROM using physically populated latch/address lines;
- an end-of-M2 mapper latch;
- register bits 0-2 selecting the PRG bank;
- register bit 4 directly selecting CIRAM A10 for single-screen nametable selection;
- 8 KiB volatile CHR RAM with normal cartridge PPU read/write behavior;
- no PRG RAM and no cartridge IRQ source;
- NES 2.0 submapper 1 = no bus conflicts;
- NES 2.0 submapper 2 = AND-style bus conflicts;
- legacy/unspecified Mapper 7 follows the established no-bus-conflict compatibility convention.

`CIRAM A10` is intentionally exposed as a live `ICompiledCombinationalComponent` output rather than a static compiler fact. `/CIRAM-CE` remains a state-independent A13-derived output and may be folded by the generic topology compiler.

## Validation coverage added

The v2.33.0 source adds 14 AxROM test cases covering:

- 32 KiB PRG bank selection and fitted-ROM address-line masking;
- live single-screen CIRAM page selection;
- the prohibition on folding live CIRAM A10 as static topology;
- 8 KiB CHR-RAM read/write behavior;
- default and NES 2.0 bus-conflict variants;
- physical falling-M2 latch timing;
- compiled bus write completion phase;
- generic-compiled versus raw-physical execution parity;
- invalid CHR-ROM/PRG-RAM/four-screen variants;
- Mapper-7 factory composition.

The expected Release suite is 346 tests. This environment cannot run .NET, so that result must be validated locally before release claims are made.

## Smoke ROM

`samples/axetos-axrom-bank-switch.nes` is an iNES Mapper-7 image. It switches to PRG bank 2 and CIRAM nametable page 1, then remains in the selected 32 KiB bank for deterministic diagnostics.

Expected shutdown diagnostic:

```text
AxROM core:  bank=$12 selected=2, nametable=1, ...
```
