# AxetosOS.Products.NES v2.19.0

## MMC1 raster-split CPU provenance trace

The frame 1-1850 Alien Syndrome capture confirms that CHR bank 1 is being used as a deliberate mid-frame raster resource throughout the game. The title sequence changes CHR1 twice during visible rendering while MMC1 control/mirroring, CHR0 and PRG state remain stable. v2.18.0 changed the live CHR read window but did not alter the visible artifact, so v2.19.0 stops changing hardware speculatively and instruments the CPU-side cause of those mapper commits.

### Host-side timing provenance

`CartridgeVideoTraceCollector` now receives the active RP2A03 only as a DesktopHost diagnostic dependency. While cartridge-video capture is active it samples existing public CPU/PPU diagnostic state at the existing one-CPU-cycle trace cadence and records, for each PPU-visible MMC1 register change:

- RP2A03 CPU/APU cycle count;
- cycles elapsed since the most recently observed RP2C02 NMI falling edge;
- CPU PC, current opcode and microcycle state;
- current CPU bus address and read/write direction;
- completed instruction and interrupt counts;
- whether NMI remains pending;
- most recent sprite-zero-hit timing in the same frame;
- DMC memory-read/stall deltas since NMI;
- OAM-DMA transfer delta since NMI.

The observation path remains outside every hardware package. No RP2A03, RP2C02 or MMC1 object retains a peer, board, simulator or host callback, and the collected values do not influence simulation.

### Diagnostic goal

A short trace around the title raster split can now distinguish whether the wrong vertical split position is caused by NMI/service timing, sprite-zero synchronization, DMC/OAM-DMA stalls, or normal instruction-cycle timing. This is diagnostic-only; there is intentionally no new mapper/PPU correctness guess in this release.

Validation target: **266 tests**. User-local `dotnet test` remains the acceptance gate.
