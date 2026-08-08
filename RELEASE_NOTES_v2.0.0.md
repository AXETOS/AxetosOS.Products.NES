# AxetosOS Products / NES v2.0.0

## Startup-compiled physical Famicom/NROM runtime

v2.0.0 is the first major runtime-architecture experiment after the v1.x physical-chip completion and profiling work. The physical motherboard/chip model remains the source of truth, but a fully assembled Famicom + mapper-0 cartridge is now compiled once at startup into direct execution routes for the fixed high-traffic physical wiring.

### Compiled physical routes

- MASTER.CLK direct fan-out to RP2A03 and RP2C02 package clock pins.
- CPU A0-A15 as one 16-trace compiled route.
- CPU D0-D7 as one eight-trace shared electrical route.
- PPU AD0-AD7 as one eight-trace shared electrical route.
- PPU A8-A13 as one six-trace compiled route.
- SN74LS373 Q0-Q7 -> CIRAM A0-A7 as one eight-trace compiled route.

The five parallel routes plus MASTER.CLK cover 47 physical traces.

### Preserved hardware semantics

- Every physical net still has an independent resolved digital level.
- Strong/weak drive priority, Hi-Z, unknown and contention behavior remain per physical bit.
- Every connected package pin still receives the resolved level before a receiving chip may react.
- Bidirectional pins still sample their physical bus while refusing self-generated input activation when actively driving.
- Chip-owned wake gates, edge activation and divider counters remain inside the physical package pins/chips.
- One package reaction remains atomic at its package boundary across compiled buses and ordinary scalar traces.
- No CPU-to-ROM shortcut, PPU-to-memory shortcut, software scheduler, signal queue, or skipped master clock pulse is introduced.

### A/B reference mode

The desktop host accepts `--reference-runtime` for Famicom. It disables the compiled machine in the same v2.0.0 executable and runs the legacy per-trace physical runtime. This provides an exact same-build benchmark/control path.

### Validation target

Expected test count after this patch: **224 tests**.

The patch was mechanically checked in the packaging environment, but the .NET SDK is not available there. Run `dotnet test` locally before accepting the release.
