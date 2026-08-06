# AxetosOS Products / NES

A modular AxetosOS hardware product that builds NES-family machines from reusable, pin-driven virtual hardware modules.

## Current status

**VirtualHardware version: v0.82.0**

The Famicom composition boots and runs real NROM software through the virtual RP2A03, RP2C02, RAM, address latch, controller hardware, cartridge board, pins, nets and clocks. Video comes from the RP2C02 output and audio comes from the RP2A03 DAC output.

I am actively improving performance with the goal of reaching full real-time execution without compromising the modular, pin-driven hardware design. Donkey Kong on the Famicom board is currently used as the main performance and correctness benchmark.

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

## v0.82.0 — motherboard phase-root execution routes

- Extends the motherboard-owned clock plan with a compiled route to the actual clock net.
- The motherboard validates and caches the phase root from its assembled wiring.
- Each clock transition still drives the real oscillator pin and resolves the real electrical net.
- The known phase-root net is settled directly before the generic causal event queue continues with affected chips and downstream nets.
- Rewiring or replacing hardware invalidates the cached route through the board topology revision.
- The route is generic infrastructure: any motherboard can compile a known source transition from its own wiring.
- Keeps the generic simulator as a safe fallback whenever a source route cannot be applied.

This is the first execution-plan step that uses motherboard knowledge to avoid rediscovering a static signal route at runtime while preserving all dynamic chip behavior.

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
