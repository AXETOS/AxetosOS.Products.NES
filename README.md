# AxetosOS Products / NES

A modular AxetosOS hardware product that builds NES-family machines from reusable, pin-driven virtual hardware modules.

## Current status

**VirtualHardware version: v0.81.0**

The Famicom composition boots and runs real NROM software through the virtual RP2A03, RP2C02, RAM, address latch, controller hardware, cartridge board, pins, nets and clocks. Video comes from the RP2C02 output and audio comes from the RP2A03 DAC output.

The current performance benchmark is Donkey Kong on the Famicom board. v0.79.0 sustained approximately 12.8 FPS on the development machine. v0.80.0 was measured as a regression, so its grouped net fan-out implementation has been removed from the active baseline. v0.81.0 starts the motherboard-owned compiled execution-plan architecture.

## Architecture

AxetosOS products are assembled from reusable modules. In this product:

- **chips** are independent modules that operate through physical virtual pins;
- **motherboards** are product composition roots that select chips and define wiring, clocks, power, reset, slots and outputs;
- **cartridges** are plug-in hardware compositions;
- **the simulator** provides generic electrical resolution, scheduling and topology compilation;
- **the running NES** emerges from the assembled motherboard, chips and cartridge.

The shared engine does not contain direct CPU-memory, PPU-tile, mapper, renderer or APU shortcuts. Motherboard-specific execution plans may reuse known static board flow, but they must still execute the installed chip modules and resolve the real virtual wiring.

## Included motherboard compositions

- Japanese Famicom
- NTSC NES
- PAL NES

A different or enhanced motherboard can reuse the same chip modules, replace chips, add memory or peripherals, and compile a different execution plan from its own topology.

## v0.81.0 — motherboard-owned compiled clock execution

- Adds a reusable `CompiledClockExecutionPlan` for oscillator-driven motherboards.
- Famicom, NTSC NES and PAL NES now own and invoke their compiled master-clock plans.
- PAL also owns an independent compiled CIC-clock plan.
- Validates topology once per requested cycle batch instead of rediscovering it for every half-cycle.
- Detects chip additions and wiring changes through an explicit board topology revision.
- Continues to toggle the real oscillator, resolve real nets and execute real chip contracts after every half-cycle.
- Removes the v0.80.0 grouped fan-out regression and restores the faster v0.79.0 pin-dispatch behavior.
- Establishes the execution-plan boundary needed for larger motherboard-specific phase plans.

## Repository layout

```text
src/Products/NES/
  AxetosOS.Products.NES.VirtualHardware/   reusable chips, boards, wiring and simulator
  AxetosOS.Products.NES.DesktopHost/       native video/audio/input host
  AxetosOS.Products.NES.HeadlessHost/      diagnostic host
  AxetosOS.Products.NES.Abstractions/       shared contracts
  AxetosOS.Products.NES.Cartridges/         cartridge metadata and loading
  AxetosOS.Products.NES.Hardware/           established reference implementation

tests/AxetosOS.Products.NES.Tests/          hardware and regression tests
```

## Build and test

From the repository root:

```powershell
dotnet test
```

## Run a ROM through VirtualHardware

```powershell
dotnet run -c Release --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost -- "C:\ROMs\Game.nes" --board famicom
```

Available board selectors include `famicom`, `ntsc`, `pal-a`, `pal-b`, and `auto`.

The desktop title reports current, minimum, maximum and average FPS. Moving or resizing the window can temporarily distort the minimum measurement.

## Hardware-simulation rules

1. Chips remain independently testable and reusable.
2. Motherboards own composition and wiring, not replacement chip behavior.
3. ROM data changes dynamic chip behavior; it does not alter motherboard topology.
4. Optimized execution may precompile static board flow but may not bypass pins, nets, clocks, buses or installed chips.
5. Replacing a chip, cartridge or connection invalidates and rebuilds the compiled topology.
6. Generic execution remains the correctness fallback.

## Performance direction

The target is native real-time operation while preserving the hardware model. Work is focused on:

- motherboard-owned phase plans;
- topology-derived dependency routes;
- dense pin/net/component indexes;
- packed buses and changed masks;
- explicit sequential boundaries;
- validation between generic and compiled execution.

The Famicom is the first demanding benchmark, not a special case inside the generic engine.

## License

MIT. See [LICENSE](LICENSE).
