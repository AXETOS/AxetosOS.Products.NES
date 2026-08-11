# AxetosOS Products / NES v2.48.0

## Konami VRC6 physical cartridge family

This release adds replaceable Konami VRC6 cartridge hardware for mapper 24 (VRC6a) and mapper 26 (VRC6b).

### Cartridge circuitry

- VRC6a direct register-line decode and VRC6b A0/A1-swapped package decode.
- Switchable 16 KiB PRG region, switchable 8 KiB PRG region and fixed final 8 KiB bank.
- Eight CHR bank registers with the VRC6 1 KiB and grouped 2 KiB modes.
- Full VRC6 banking-mode register behavior for CIRAM routing and CHR-ROM nametable sourcing.
- Optional 8 KiB work RAM gated by the package banking-control register.
- Reuse of the validated Konami VRC IRQ divider/counter for scanline and cycle modes.
- Raw physical and startup-compiled bus paths keep end-of-cycle mapper/IRQ/audio timing aligned.

### VRC6 expansion-audio circuitry

- Two independent 16-step pulse generators with volume, duty/constant-output mode, 12-bit frequency and enable state.
- One 14-step saw generator with 6-bit accumulator rate and retained accumulator/output state.
- Global VRC6 audio halt and frequency-scaling control.
- Cartridge-local DAC/output diagnostics and clock/register counters.

The VRC6 sound generators remain chip-owned cartridge circuitry. They are not mixed into host PCM through a mapper-specific shortcut; audible expansion audio requires a reusable physical analog connector/net path shared by 5B, VRC6, Namco 163, VRC7 and MMC5-class hardware.

### Validation scope

The patch adds VRC6 package/address-line, PRG/CHR/nametable, RAM, IRQ, audio, compiled-edge and raw-vs-compiled conformance coverage plus a synthetic mapper-24 VRC6 ROM. The incoming validated baseline is v2.47.1 at 623/623 Release tests. Runtime test status for v2.48.0 must be established with `dotnet test -c Release` on the development machine.
