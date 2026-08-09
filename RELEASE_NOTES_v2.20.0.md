# AxetosOS.Products.NES v2.20.0

## MMC1 raster-trigger provenance trace

The v2.19.0 full Alien Syndrome capture resolves several of the original timing suspects without another emulation change.

Across the title-phase CHR1 workload, every visible bank commit is observed at the same common writer site (`PC=$F895`, opcode `$8D`, `FetchOpcode`). DMC activity is zero in the captured interval and the recurring title frames all perform the same single OAM DMA, reported by the existing counter as 256 transferred bytes. The two CHR1 commits within each complete title frame are separated by exactly **8,692 CPU cycles and 4,380 completed instructions**. The raster variation is therefore introduced before the common MMC1 writer and then propagated through an invariant CPU sequence.

The first commit falls into discrete NMI-relative families rather than drifting continuously: approximately 4,704-4,713 cycles after NMI near scanline 20, approximately 5,136-5,143 near scanline 24, and approximately 5,870-5,879 near scanline 30. Sprite-zero activity also differs between those families, but v2.19.0 only recorded the PPU's resulting hit state; it did not show whether the CPU actually polled PPUSTATUS or which caller reached the common mapper writer.

### New host-only provenance

`CartridgeVideoTraceCollector` now observes the already-public RP2A03 physical bus state at the existing 12-master-clock precision cadence and records PPUSTATUS reads, including mirrors of `$2002`.

Each PPU-visible MMC1 commit now adds:

- `ppustatus-from-nmi=total/clear/set`;
- `last-ppustatus=...` with age, raster, returned bus byte and CPU PC/opcode;
- `last-s0-clear-status=...`;
- `first-s0-set-status=...`;
- `fetch-tail=...`, the sixteen most recent opcode-fetch addresses and sampled opcode bytes.

The previous OAM field is renamed to `oam-dma-bytes-from-nmi` to state what `DmaTransferCount` actually measures: completed OAM byte transfers, not the 513/514-cycle total DMA duration.

### Architectural boundary

All new state belongs to the DesktopHost diagnostic collector. It samples public package diagnostics and the physical CPU bus and never feeds a value back into execution. RP2A03, RP2C02, MMC1, the motherboard and the whole-circuit compiler remain unaware of the collector and of one another beyond their genuine pins/topology.

This release deliberately changes **no CPU, PPU, MMC1, motherboard or compiler execution behavior**.

Validation target: **266 tests**. User-local `dotnet test` remains the acceptance gate.
