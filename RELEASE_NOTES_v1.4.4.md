# AxetosOS Products / NES v1.4.4

RP2C02/RP2C07 internal phase and decoded-control performance sweep.

v1.4.3 kept the physical architecture intact but regressed measured normal Release speed (Mario 21.83 FPS and Donkey Kong 20.86 FPS versus the v1.4.2 22.55 / 21.04 FPS baseline). v1.4.4 removes the losing branch-heavy SRAM/latch/PPU experiment and concentrates on reducing legitimate work inside the physical PPU package.

## Changes

- Reverts the v1.4.3 HM6116 and SN74LS373 branch-heavy direct-reaction experiments to the faster v1.4.2 implementations. Physical pin gating and retained-state behavior from earlier validated releases remain intact.
- Reverts the v1.4.3 RP2C02/RP2C07 per-dot VRAM/NMI output-stage gating that added hot-path bookkeeping and restores the faster retained-output publication path from v1.4.2.
- Keeps the useful v1.4.3 RP2A03/RP2A07 controller-input stage gating.
- Treats PPUCTRL and PPUMASK as retained package registers feeding decoded internal control lines. NMI enable, CPU VRAM increment, pattern-table selects, sprite height, rendering enables, left-column enables, greyscale and emphasis are decoded only when the register changes.
- Skips background/sprite rendering-pipeline calls completely during post-render and vblank scanlines; raster/vblank/NMI and external CPU/VRAM behavior continue normally.
- Stops extracting background pixels during 321-336 prefetch dots and stops recomputing the pixel DAC during 257-340 fetch/housekeeping dots, where the visible pixel sample output is not driven.
- Combines sprite priority selection with all active sprite X-counter/pattern-shifter advancement in one pass through the eight physical sprite output units.
- Copies only the active sprite-output units at the scanline handoff instead of copying all eight software slots unconditionally.
- Mirrors the same internal optimization structure in NTSC RP2C02 and PAL RP2C07, preserving PAL emphasis-channel mapping.

## Architecture

Unchanged: dumb motherboard, smart physical chips. The motherboard still resolves and delivers every physical level and knows no `/CS`, `/OE`, `/WE`, CPU/PPU, edge, rendering or receiver-interest semantics. Package pins retain delivered levels; RP2C02/RP2C07 own their clock phases, register decode, rendering pipelines, VRAM sequencer and output pins. No signal queue, scheduler, settle pass or skipped physical clock is introduced.

Run `dotnet test` first. The expected test count remains 215. Then benchmark normal Release Mario and Donkey Kong against the v1.4.2 baseline (22.55 / 21.04 FPS).
