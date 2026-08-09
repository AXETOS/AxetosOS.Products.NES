# AxetosOS.Products.NES v2.22.0

## Exact MMC1 CHR-read provenance around sprite zero

The v2.21.0 Alien Syndrome capture moves the fault path back onto the cartridge/background side. Sprite zero itself is stable across the title window: OAM0 keeps Y=$10, tile=$D0 and attributes=$20; its fetched low/high pattern data is identical from frame to frame; and the sprite is neither masked nor rejected by the ordinary sprite mux. A missed sprite-zero hit occurs only when those same opaque sprite pixels see transparent background pixels.

That is significant because PPUCTRL=$90 selects the $1000 background pattern table while 8x8 sprites use the $0000 sprite pattern table. With MMC1 control=$1E in 4 KiB CHR mode, the sprite therefore comes through CHR0=$18 while the background under it comes through CHR1, which Alien Syndrome is actively switching between $18 and $19 during the visible frame.

v2.22.0 changes no CPU, PPU, mapper, motherboard, compiler or cartridge execution behavior. It adds an MMC1-owned diagnostic output at the exact compiled PPU read-completion callback. Each captured CHR read reports:

- the cartridge PPU-read sequence number;
- logical PPU pattern address;
- physical CHR ROM byte address;
- selected 4 KiB CHR bank;
- returned CHR data byte;
- retained MMC1 control, CHR0, CHR1 and PRG registers at the read-complete instant.

`CartridgeVideoTraceCollector` pairs those mapper-owned read-complete events with RP2C02 rendering-fetch events. For background pattern fetches on sprite-zero scanlines 17-24, every `CART FRAME` line now adds `s0-bg-chr=...`, including:

- total background pattern fetches in the sprite-zero vertical window;
- exact mapper-read matches, misses and unmatched non-rendering CHR reads;
- physical 4 KiB bank mask;
- the exact CHR1 transition raster inside that window, if one occurs;
- per-bank read counts and hashes over logical address/data;
- an exact hash including logical address, physical CHR address, data and CHR1 register state.

For compiled execution this removes the remaining one-CPU-cycle ambiguity of host-side `InspectPpuMapping`: the provenance is emitted by MMC1 itself when it returns the byte that RP2C02 samples. The existing physical-hardware model remains unchanged; diagnostics never feed execution and no package gains a peer reference.

Validation target remains **266 tests**. User-local `dotnet test` remains the acceptance gate.
