# AxetosOS.Products.NES v2.16.3

## Precise MMC1 raster-correlation trace

The first v2.16 cartridge-video capture identified a repeatable mapper-side signature in the affected Alien Syndrome window: `control`, `chr0` and `prg` remain stable while `chr1` switches twice per frame. It also exposed a diagnostic timing problem: buffered MMC1 events were drained only after the normal 16,384-master-clock host batch, so their printed PPU raster coordinates could lag the real mapper commit by multiple scanlines.

v2.16.3 keeps the hardware model unchanged and makes the trace precise enough to test that lead:

- while cartridge-video capture is active, DesktopHost advances/drains in 12-master-clock batches (one CPU bus-cycle period on the Famicom clock tree);
- normal non-trace execution retains the 16,384-master-clock batch;
- MMC1 register trace events now carry cartridge-local PPU read/write counters sampled at the exact register commit;
- `CART MMC1` output reports the exact commit counters plus the externally observed raster and PPU-read drain lag;
- completed framebuffer hashes are retained until the RP2C0x frame counter advances, so the pre-render scanline remains part of the same single frame summary instead of producing a duplicate 137-fetch record.

This is diagnostic-only. No CPU, PPU, MMC1, cartridge connector, motherboard or compiler execution behavior changes.

Validation target: **262 tests**. User-local `dotnet test` remains the acceptance gate.
