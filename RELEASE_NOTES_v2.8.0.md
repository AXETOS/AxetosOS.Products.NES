# AxetosOS Products / NES v2.8.0

## Generic topology-compiled physical circuit experiment

This release adds a third execution mode while preserving both existing modes:

- default: the existing fused Famicom/NROM runtime (~60 FPS proof-of-performance);
- `--reference-runtime`: full per-trace physical execution;
- `--compiled-generic`: new topology-only generic compilation experiment.

### Generic compiler invariants

The generic compiler has no product or signal semantics. It compiles only:

- physical package pins;
- output-driver states and drive strength;
- input-capable receiver pins;
- chip-owned input activation metadata;
- fixed physical net membership.

It does not identify or special-case NES, Famicom, RP2A03, RP2C02, SRAM, cartridge, mapper, CPU bus, PPU bus or any named signal. The same compiler is exposed through `VirtualHardwareSimulator.SetGenericCompiledTransportEnabled(true)` and is validated against the non-NES pin-wired example computer.

At runtime, the compiler removes repeated `DigitalNet` topology interpretation but preserves physical behavior: each changed driver resolves its actual net, all connected package pins receive the resolved level, contention and Hi-Z remain electrical states, and chips react only through their existing package input masks. Atomic multi-output package changes are still presented before any destination package executes.

### Baseline restoration

The patch is built from v2.6.0 and includes the v2.6 RP2A03/RP2A07 files so it can be applied over the current v2.7.1 experiment and cleanly remove the losing v2.7.x CPU-core changes.

### Validation

The patch-generation environment has no .NET toolchain. Static structure and topology checks were run. Expected local suite: **230 tests**. Benchmark all three modes before deciding whether generic compilation is a keeper.
