# AxetosOS Products / NES v2.34.0

## Mapper 11 / Color Dreams physical cartridge hardware

v2.34.0 adds Mapper 11 without adding Mapper-11 knowledge to the motherboard or generic whole-circuit compiler.

The new `ColorDreamsCartridge` owns the physical cartridge behavior:

- one switchable 32 KiB CPU PRG-ROM window at `$8000-$FFFF`;
- up to four physically addressable 32 KiB PRG banks (128 KiB);
- one switchable 8 KiB PPU CHR-ROM window at `$0000-$1FFF`;
- up to sixteen physically addressable 8 KiB CHR banks (128 KiB);
- one shared 8-bit end-of-M2 latch corresponding to the board's octal latch;
- latch D0-D1 selecting PRG bank address lines;
- latch D4-D7 selecting CHR bank address lines;
- D2-D3 retained in the physical latch but not used for PRG/CHR banking;
- fixed horizontal/vertical CIRAM wiring selected by cartridge board metadata;
- standard AND-style CPU/PRG-ROM bus conflicts;
- no PRG RAM, CHR RAM, battery-backed memory or cartridge IRQ source.

The rare no-bus-conflict Mapper-11 prototype wiring is not silently guessed. Mapper 11 currently has no defined non-zero NES 2.0 submapper contract in this model, so unknown non-zero submappers are rejected until they can identify a physical board unambiguously.

No compiler file is changed by this mapper implementation.

## Validation coverage added

The v2.34.0 source adds 13 test cases covering:

- power-on PRG/CHR bank zero state;
- simultaneous PRG and CHR switching through the shared latch;
- independent fitted-ROM address-line masking;
- retention of the full octal latch value;
- AND-style bus-conflict behavior;
- fixed horizontal and vertical CIRAM routing;
- physical falling-M2 latch timing;
- compiled write completion timing;
- CHR-ROM read-only behavior;
- generic-compiled versus raw-physical execution parity;
- invalid RAM/four-screen/ROM geometry rejection;
- undefined Mapper-11 submapper rejection;
- Mapper-11 cartridge-factory composition.

The expected Release suite is 359 tests. This environment cannot run .NET, so that result must be validated locally before release claims are made.

## Smoke ROM

`samples/axetos-colordreams-bank-switch.nes` is an iNES Mapper-11 image with four PRG banks and sixteen CHR banks. It performs a bus-conflict-safe write of `$31`, selecting PRG bank 1 and CHR bank 3, then remains in the selected PRG bank for deterministic diagnostics.

Expected shutdown diagnostic:

```text
Color Dreams: bank=$31, prg=1/4, chr=3/16, ...
```
