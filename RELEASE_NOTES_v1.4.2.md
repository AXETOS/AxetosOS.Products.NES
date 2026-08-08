# AxetosOS Products / NES v1.4.2

Profiler-guided direct shared-bus resolver and steady-state package-clock sweep.

v1.4.1 proved that reducing operation count alone is not sufficient: its incremental strong/weak driver aggregate made normal Release execution slower (Mario 20.21 FPS, Donkey Kong 18.24 FPS versus the v1.4.0 22.20 / 19.27 FPS baseline). v1.4.2 removes that duplicated bookkeeping and returns hot shared traces to direct physical pin-state resolution, with compact unrolled paths for the three/four-driver topologies that dominate the NES CPU D and PPU AD buses.

## Changes

- Removes the v1.4.1 per-net incremental driver-state cache, counters and per-pin driver index.
- Resolves 3-driver and 4-driver traces with fixed direct pin-state paths; uncommon larger fan-in traces use a direct scan.
- Preserves package-boundary atomic output publication: all changed output pins already hold their final drive state before any affected trace is resolved.
- Retains v1.4.1's useful chip-owned wake-gate storage path, output-only delivery path, and fixed-width DigitalBus sampling/drive/release optimizations.
- Adds chip-internal steady-state clock-only paths to RP2C02/RP2C07 and RP2A03/RP2A07 so the overwhelmingly common clock activation does not repeatedly decode unrelated package input masks.
- Adds four-driver regression coverage for weak contention, strong dominance, unknown drive and release.

## Architecture

Unchanged and non-negotiable: the motherboard transports physical electrical state only. It does not know `/CS`, `/OE`, `/WE`, clock semantics, receiver interest, or chip state. Every resolved level is delivered to every physically connected package pin. Package pins/chips own activation and clock division. There is no signal queue, scheduler, settle engine, receiver-aware suppression or skipped physical clock pulse.

Run `dotnet test` first. If all 215 tests pass, benchmark normal Release Mario and Donkey Kong against v1.4.0 (22.20 / 19.27 FPS) and v1.4.1 (20.21 / 18.24 FPS).
