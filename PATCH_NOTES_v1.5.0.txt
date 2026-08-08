# AxetosOS Products / NES v1.5.0

RP2C02/RP2C07 hardwired internal chip-core rewrite.

The v1.4.x profiler consistently identified the physical PPU package as the largest component cost. v1.5.0 stops adding software-style branch shortcuts to the old broad PPU pipeline and instead changes the package-internal execution model: retained raster counters feed fixed decode lines, internal transaction state directly changes output stages, and inactive circuitry is disconnected by the chip's own retained state.

## RP2C0x internal timing decoder

- Adds a shared immutable 341-dot `PpuDotDecoder` used by both RP2C02 and RP2C07.
- Background shift/load/fetch phases, coarse/fine scroll transfer points, sprite activation/evaluation/fetch phases and visible pixel clocks are decoded once from the physical horizontal counter schedule.
- The steady-state PPU clock path executes the decoded internal circuits directly rather than repeatedly rebuilding dot ranges and modulo phases through broad `AdvanceBackgroundPipeline` / `AdvanceSpritePipeline` routines.
- NTSC odd-frame timing and PAL full-raster timing remain owned by their respective physical PPU packages.

## Rendering circuit semantics

- Shared rendering/fetch circuitry is active whenever either background or sprite rendering is enabled.
- Background output can remain transparent while sprite-only rendering still clocks the shared background VRAM fetch circuitry, matching the physical PPU rendering sequencer.
- When both layers are disabled, forced blank disconnects background fetch and sprite evaluation/fetch circuitry while the color output stage remains active.
- Visible background pixel extraction is performed only on visible decoder clocks; the sprite pixel mux/counter path is likewise limited to visible pixel clocks.
- Sprite evaluation/secondary-OAM clearing is restricted to visible scanlines; the pre-render line preserves the prior evaluation result for its sprite-fetch phase instead of running a software-style extra evaluation pass.

## Event-driven internal output stages

- A VRAM transaction now drives its package pins when the transaction state changes instead of revisiting all VRAM package outputs on every PPU dot.
- Transaction start presents the multiplexed address/ALE phase immediately.
- The next transaction phase presents the read/write data phase.
- Completion returns the bus to idle only when no new physical fetch starts in the same package reaction, preserving atomic package publication.
- Vblank changes directly update the package-local NMI gate; PPUCTRL changes update that same gate immediately. The old per-dot `DriveNmi` polling path is removed.
- The visible palette output uses a retained 32-entry physical palette-mirror decoder.

## Conformance coverage

Adds NTSC and PAL tests proving that sprite-only rendering still clocks the shared background fetch circuit, background-only rendering still clocks sprite evaluation, and forced blank disconnects both render-fetch and sprite-evaluation circuits. Existing PPU VRAM, NMI, vblank suppression, sprite, palette, scroll and raster tests remain applicable. The existing PPUDATA transaction test now also verifies that the event-driven VRAM pins return to their physical idle state at completion.

Expected test count after this patch: 221.

## Architecture

Unchanged at the package boundary: the motherboard is electrically dumb and always transports physical levels. Every package pin stores the delivered level. RP2C02/RP2C07 alone own raster timing, internal decoder lines, rendering activation, VRAM sequencing, vblank/NMI state and package outputs. No signal queue, scheduler, settle pass, receiver-aware motherboard suppression or skipped physical clock is introduced.
