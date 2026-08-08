# AxetosOS Products / NES v1.5.1

RP2C02/RP2C07 parallel background-shifter optimization.

The v1.5.0 sampled profile showed that the new hardwired PPU core substantially reduced the cost attributed to package-output, sprite, raster and visible-video stages. Background and VRAM work remained the largest named internal PPU sections, so v1.5.1 keeps the hardwired timing model and optimizes the retained background datapath itself.

## Parallel physical shifter representation

The PPU still owns four independent 16-bit background shift registers: pattern low, pattern high, attribute low and attribute high. They are now packed into four 16-bit lanes of one 64-bit host value. A lane-boundary mask prevents carry between lanes, so one host shift is exactly equivalent to clocking all four physical shifters once. Public diagnostic access to the two pattern shifters is preserved.

The next-tile load state is packed in the same four-lane layout. Pattern fetch completion updates the corresponding pattern byte; attribute fetch completion expands the two palette bits into the two physical 0x00/0xFF attribute lanes. A tile-load edge merges all four low bytes into the retained shifters in one operation.

## Fine-X mux

The fine-X register now retains its decoded tap shift when the first PPUSCROLL write changes fine X. Visible pixel extraction samples that retained tap from all four shifter lanes rather than recomputing the selector mask for every visible pixel.

## Hot chip-local helpers

The background shift/load/pixel helpers and the rendering VRAM transaction/address/data phase helpers are marked for aggressive inlining. This does not suppress any physical PPU bus transition; it only removes avoidable host call overhead inside the physical package.

## Architecture

Unchanged. Every master clock edge reaches the package, every external PPU pin transition crosses the physical motherboard traces, and the motherboard has no rendering or receiver-interest semantics. All optimization is confined to the internal representation and direct reaction paths of RP2C02/RP2C07.

Expected test count remains 221.
