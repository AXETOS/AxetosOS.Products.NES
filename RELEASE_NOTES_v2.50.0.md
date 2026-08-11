# AxetosOS Products / NES v2.50.0

## Konami VRC7 / Mapper 85

This release adds VRC7 as replaceable physical cartridge hardware without adding mapper semantics to the motherboard or generic whole-circuit compiler.

### Cartridge hardware

- Three switchable 8 KiB PRG windows plus fixed final 8 KiB.
- Eight switchable 1 KiB CHR-ROM windows.
- Vertical, horizontal and both single-screen CIRAM routing.
- Optional 8 KiB SRAM gated by VRC7 control bit 7.
- VRC7 control bit 6 mutes FM output and disregards FM port writes while set.
- VRC7 x008/x010 register alias normalization, with $9010/$9030 retained as dedicated FM address/data ports.
- Shared validated Konami VRC IRQ block with full-byte reload, cycle/scanline modes and acknowledge behavior.

### Chip-local FM block

`KonamiVrc7Audio` models the VRC7-visible six melodic two-operator channels, register select/data ports, writable custom patch, fifteen VRC7 mask-ROM patches, F-number/block/key/sustain/instrument/volume state, integer phase/envelope/operator evolution and a retained cartridge DAC node. Internal FM output advances at the OPLL clock/72 cadence, equivalent to one sample per 36 NTSC CPU cycles.

The FM node is intentionally not mixed into host PCM through a mapper-specific shortcut. A future generic physical analog cartridge path can transport VRC7, VRC6, Namco 163, Sunsoft 5B and MMC5 expansion audio consistently.

### Diagnostics and conformance

Desktop shutdown diagnostics report VRC7 PRG/CHR/control/SRAM state, normalized last register address, IRQ activity, FM register traffic, key-ons, sample clocks, DAC edges and all six channel register states. A Mapper-85 synthetic ROM exercises banking, SRAM, FM register traffic and cycle IRQ behavior through the same physical desktop-host path used by commercial games.

Source behavior was cross-checked against the maintained MesenCE VRC7 mapper implementation and its VRC7-mode emu2413 integration.
