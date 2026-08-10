# AxetosOS Products / NES v2.38.0

## Mapper 79 / NINA-03/NINA-06 physical cartridge

v2.38.0 adds Mapper 79 as replaceable NINA-03/NINA-06 cartridge hardware while preserving the physical cartridge boundary and generic whole-circuit compiler architecture.

### Hardware implemented

- 32 KiB switchable PRG-ROM window at CPU `$8000-$FFFF`, with up to 64 KiB fitted PRG ROM.
- 8 KiB switchable CHR-ROM window at PPU `$0000-$1FFF`, with up to 64 KiB fitted CHR ROM.
- Address-decoded control latch selected by the cartridge connector condition `010x xxx1 xxxx xxxx` (`$4100-$41FF`, `$4300-$43FF`, ... `$5F00-$5FFF`).
- CPU D3 drives the PRG bank address line; CPU D0-D2 drive the CHR bank address lines.
- Control data is sampled on completion of the M2-qualified CPU write cycle.
- No CPU/PRG-ROM bus conflict: the register is below the cartridge PRG-ROM window.
- Fixed horizontal/vertical CIRAM wiring from the cartridge image metadata.
- No PRG RAM, CHR RAM, IRQ or expansion audio.
- Fitted ROM address lines are modeled directly; absent bank lines do not use software modulo normalization.

NINA-03 and NINA-06 differ in lockout circuitry, not in the mapper-79 PRG/CHR/mirroring path modeled at the current normalized cartridge connector.

### Compiler boundary

No mapper-79 logic was added to the motherboard or generic compiler. The control-register decode is exposed to compiled execution as ordinary physical pin requirements (R/W, `/ROMSEL`, CPU A14, A13 and A8), allowing the generic topology compiler to pre-resolve the route without learning NES or mapper semantics.

### Validation coverage

Adds 23 test cases covering:

- PRG and CHR bank selection;
- fitted ROM address-line masking;
- all decoded register-window forms and nearby non-selected addresses;
- no-bus-conflict behavior;
- fixed H/V mirroring;
- physical falling-M2 latch timing;
- A15/`/ROMSEL` address disambiguation;
- compiled descriptor pin conditions and read-only ROM targets;
- generic-compiled versus raw-physical execution parity;
- invalid geometry/RAM/four-screen/battery rejection;
- undefined submapper rejection;
- Mapper-79 cartridge factory composition.

The expected Release suite increases from the validated **390 / 390** v2.37.1 baseline to **413 tests**. Local .NET validation is still required.

### Smoke ROM

`samples/axetos-nina-bank-switch.nes` writes `$0D` to `$4100`, selecting:

- PRG bank 1 of 2;
- CHR bank 5 of 8.

The desktop host prints a `NINA-03/06:` diagnostic line at shutdown so the physical latch state and cartridge traffic can be inspected directly.
