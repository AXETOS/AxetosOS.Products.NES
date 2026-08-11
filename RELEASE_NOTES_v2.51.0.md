# AxetosOS Products / NES v2.51.0

## Nintendo MMC5 / Mapper 5 physical cartridge

- Adds the Nintendo MMC5 ASIC as replaceable Mapper-5 cartridge hardware; the motherboard and generic whole-circuit compiler remain unaware of MMC5 semantics.
- Implements all four PRG banking modes, `$5113-$5117` bank registers, fixed final-ROM rules and banked cartridge RAM/NVRAM selection.
- Implements the two-register PRG-RAM write-protect gate and licensed-board RAM socket mirroring/open-socket behavior, with the wider 64/128 KiB NES 2.0 decode retained where explicit topology requires it.
- Implements all four CHR banking modes, the A/B CHR register sets used by 8x16-sprite rendering, `$5130` upper bank bits, and banked CHR ROM or CHR RAM.
- Adds the MMC5 1 KiB ExRAM and all four CPU/PPU access modes.
- Implements `$5105` independent nametable routing among CIRAM page 0, CIRAM page 1, ExRAM and fill mode, plus `$5106/$5107` fill tile/color generation.
- Implements extended-attribute mode, including per-tile palette substitution and the ExRAM-selected 4 KiB CHR bank path.
- Implements vertical split control, scroll, ExRAM tile/attribute substitution and split-region 4 KiB CHR banking.
- Adds PPU-read-snooped in-frame/scanline detection, `$5203/$5204` scanline IRQ behavior, vector-read frame reset and open-drain IRQ drive.
- Adds the `$5205/$5206` 8x8 hardware multiplier.
- Adds chip-local MMC5 expansion audio: two no-sweep pulse channels, fixed ~240 Hz length/envelope clock, `$5015` status/enable, direct PCM and read-mode/PCM-IRQ state. Pulse timer/output work is event-driven internally without skipping any package CPU clocks.
- Expansion-audio DAC state remains inside the cartridge. No mapper-specific path is added to host PCM; generic analog cartridge/net infrastructure remains the required boundary for audible expansion mixing.
- Adds startup-compiled targets for CPU/PPU memory, register writes, PPU read snooping and direct bus-address combinational CIRAM outputs. Raw physical propagation and compiled execution are required to agree on the whole-machine synthetic program.
- Adds native desktop shutdown diagnostics for MMC5 banking, ExRAM/PPU traffic, extended/split fetches, IRQ/multiplier and expansion-audio circuitry.
- Adds `samples/axetos-nintendo-mmc5-irq-audio.nes` exercising Mapper-5 PRG/CHR banking, ExRAM, per-nametable sources, fill, extended attributes, vertical split, protected work RAM, multiplier, IRQ and chip-local audio.
- Adds 40 MMC5 conformance tests covering banking modes, RAM protection, CHR A/B selection, ExRAM, nametable/fill modes, extended attributes, vertical split, scanline IRQ, multiplier, pulse/PCM behavior, CHR RAM, compiled completion timing, raw/compiled parity, factory routing and invalid topologies.

Validated baseline before this source patch: **719 / 719 Release tests passing through v2.50.2**. The v2.51.0 test suite must be run by the user before this release is considered validated.
