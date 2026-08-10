# AxetosOS Products / NES v2.39.0

## Mapper 227 address-latch multicart physical cartridge

v2.39.0 adds Mapper 227 as replaceable multicart hardware while preserving the physical cartridge boundary and keeping all mapper semantics out of the motherboard and generic compiler.

### Hardware implemented

- CPU `$8000-$FFFF` write-address latch: bank/mode control comes from physical CPU address lines A0-A10; CPU write data is not used for bank selection.
- Up to 1 MiB PRG ROM arranged as fitted 16 KiB banks, with absent ROM address lines modeled by physical masking rather than software modulo normalization.
- NROM-128 mode: one selected 16 KiB bank mirrored across both CPU halves.
- NROM-256 mode: one selected even/odd 32 KiB pair.
- UNROM-like mode with switchable lower 16 KiB plus fixed inner bank 7.
- Inverse UNROM-like mode with switchable lower 16 KiB plus fixed inner bank 0 in the selected outer group.
- S bit even-bank routing, L/O mode routing and mapper-controlled horizontal/vertical CIRAM A10 wiring.
- One unbanked 8 KiB CHR-RAM chip.
- Multicart CHR-RAM write protection in NROM modes, including legacy iNES compatibility.
- Four-bit solder-pad mux capable of replacing PRG A3-A0 when the m address-latch output is active on multicart hardware.
- NES 2.0 submapper 0, 1 and 2 distinctions; undefined submappers are rejected instead of approximated.
- No cartridge IRQ or expansion audio.

Battery-backed Chinese-RPG Mapper-227 boards are deliberately rejected for now because they add WRAM and omit the multicart UNROM-like modes; they are a distinct physical board variant and are not approximated as the standard multicart.

### Exact 1200-in-1 target

The supplied `1200-in-1 (J) [p1].nes` was inspected as a validation target. Its header is legacy iNES Mapper 227 with 512 KiB PRG ROM and no CHR ROM, which maps to the 8 KiB CHR-RAM multicart path implemented here. Its menu code also contains the Mapper-227 `m`-controlled low-address/solder-pad probe sequence, so legacy multicart images retain that physical mux even though NES 2.0 later identifies the feature explicitly as submapper 1. The supplied commercial ROM is not copied into this patch or the repository.

The 512 KiB geometry is important: the mapper can expose an additional PRG bank-address output on larger boards, but that line is physically absent from a 512 KiB ROM. The cartridge therefore masks the unconnected line naturally.

### Compiler boundary

No Mapper-227 semantics were added to the motherboard or generic compiler. Compiled execution sees ordinary cartridge connector bus targets, pin conditions, a completion-phase CPU write, and live combinational CIRAM outputs. Address-latch interpretation remains entirely inside the replaceable cartridge component.

### Validation coverage

Adds 23 test cases covering:

- power-on/reset bank-zero wiring;
- NROM-128 and NROM-256 modes;
- both UNROM-like fixed-bank arrangements;
- S-bit even-bank behavior;
- 512 KiB fitted-ROM masking of the unavailable high PRG address output;
- legacy iNES 512 KiB multicart geometry;
- live H/V mirroring and non-foldable CIRAM A10 routing;
- CHR-RAM write protection and submapper-0 writable behavior;
- solder-pad low-address mux behavior for legacy multicarts and NES 2.0 submapper 1;
- NES 2.0 submapper-2 fixed-inner-zero outer-bank behavior;
- physical falling-M2 address-latch timing and independence from CPU write data;
- compiled descriptor cycle phase/selection;
- generic-compiled versus raw-physical execution parity;
- invalid RAM/CHR/four-screen/geometry rejection;
- undefined submapper rejection;
- Mapper-227 cartridge factory composition.

The expected Release suite increases from the validated **413 / 413** v2.38.0 baseline to **436 tests**. Local .NET validation is required.

### Synthetic smoke ROM

`samples/axetos-mapper227-multicart.nes` deliberately uses the same legacy-iNES 512 KiB PRG / CHR-RAM geometry as the supplied 1200-in-1 image, but contains only an AxetosOS test program.

It performs three address-latch writes to exercise NROM-128, NROM-256 and UNROM-like routing before settling on `$8216`, which should leave:

- latch `$216`;
- mode `UNROM-fixed-7`;
- lower PRG bank 5;
- upper PRG bank 7;
- horizontal mirroring;
- CHR-RAM write protection disabled.

The desktop host prints a `Mapper 227:` diagnostic line at shutdown so the live address latch, decoded banks, mirroring, CHR protection, solder-pad mux and cartridge traffic can be inspected directly.
