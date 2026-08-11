# AxetosOS Products / NES v2.51.3

## MMC5 complete 50-fetch PPU geometry

- Corrects the MMC5 internal nametable-fetch geometry from the incomplete 42-event approximation to the hardware-compatible 50-event scanline sequence.
- Extends the 8x16 sprite CHR-A selection interval from tile counts 32-39 to the complete 32-47 window (sixteen nametable fetch events for eight sprite slots).
- Moves the post-sprite extended-attribute boundary from 40 to 48.
- Moves vertical-split next-scanline detection from 41 to 49 and split-column wrapping/delimiter bounds from 42 to 50.
- Retains the v2.51.2 CHR-set latch across the scanline detector reset.
- Adds a regression test proving CHR-A remains selected through tile 47 and returns to CHR-B at tile 48.
- No motherboard, game-specific or mapper-specific compiler shortcut was added.
- Expected Release suite: 761 tests.
