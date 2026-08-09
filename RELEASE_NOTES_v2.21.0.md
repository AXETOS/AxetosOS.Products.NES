# AxetosOS.Products.NES v2.21.0

## Sprite-zero pixel provenance trace

The v2.20.1 Alien Syndrome capture proves the title-phase CHR1 raster family is selected by the retained RP2C02 sprite-zero latch rather than by mapper drift. Frames entering NMI with sprite zero set perform about 131-132 `$2002` reads, of which roughly 130-131 still report bit 6 set, and leave the loop only when the PPU clears sprite zero during pre-render. Frames entering NMI with sprite zero clear perform only two clear reads and begin the otherwise-identical MMC1 sequence roughly 1,165 CPU cycles earlier.

v2.21.0 changes no emulation behavior. It extends RP2C02's already-existing captured diagnostic output so the next short trace can identify why an individual visible frame does or does not produce sprite-zero hit.

During explicit split-trace capture, RP2C02 now emits diagnostic events for:

- sprite-zero activation and retained row;
- sprite-zero pattern-low and pattern-high fetch bytes;
- opaque sprite-zero pixels over transparent background;
- opaque sprite-zero/background overlaps;
- pixels suppressed by sprite-output masking;
- the hardware x=255 no-hit case;
- an opaque overlap where the ordinary sprite mux did not select sprite zero;
- the existing authoritative sprite-zero-hit latch transition.

`CartridgeVideoTraceCollector` folds those events into each `CART FRAME` line and reports:

- PPUCTRL/PPUMASK;
- OAM0 Y/tile/attributes/X;
- sprite-zero active scanlines and row mask;
- low/high pattern-fetch counts, non-zero counts and hash;
- background-clear, overlap, masked, x=255 and not-selected pixel counts with first raster positions;
- final sprite-zero hit raster.

All hot-pixel diagnostics are gated by `SplitTraceOutput.CaptureEnabled`; normal execution does not perform or emit this provenance work. No chip gains a peer reference and no diagnostic state feeds execution.

Validation target: **266 tests**. User-local `dotnet test` remains the acceptance gate.
