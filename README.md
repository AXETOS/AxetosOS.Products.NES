# AxetosOS Products / NES

A modular AxetosOS hardware product that builds NES-family machines from reusable, pin-driven virtual hardware modules.

## Current status

**VirtualHardware version: v0.84.0**

The Famicom composition boots and runs real NROM software through the virtual RP2A03, RP2C02, RAM, address latch, controller hardware, cartridge board, pins, nets and clocks. Video comes from the RP2C02 output and audio comes from the RP2A03 DAC output.

I am actively improving performance with the goal of reaching full real-time execution without compromising the modular, pin-driven hardware design. Donkey Kong on the Famicom board is currently used as the main performance and correctness benchmark.

## Architecture

AxetosOS products are assembled from reusable modules. In this product:

- **chips** are independent modules that operate through physical virtual pins;
- **motherboards** are product composition roots that select chips and define wiring, clocks, power, reset, slots and outputs;
- **cartridges** are plug-in hardware compositions;
- **the simulator** provides generic electrical resolution, scheduling and topology compilation;
- **the running NES** emerges from the assembled motherboard, chips and cartridge.

The shared engine does not contain direct CPU-memory, PPU-tile, mapper, renderer or APU shortcuts. Motherboard-specific execution plans may use known static board flow, but they must still execute the installed chip modules and resolve the real virtual wiring.

## Included motherboard compositions

- Japanese Famicom
- NTSC NES
- PAL NES

A different or enhanced motherboard can reuse the same chip modules, replace chips, add memory or peripherals, and compile a different execution plan from its own topology.

## Power-on motherboard compilation

At power-on, ROM load or after a topology change, the motherboard builds an in-memory execution plan from the installed chips, their pin/activation contracts and the actual wiring.

The plan records:

- pin roles and package ownership;
- chip activation and gating conditions;
- direct electrical routes;
- package-level changed-input masks;
- clock and sequential boundaries;
- the components that can actually react to each signal path.

This startup work is intentionally performed once. A visible loading pause is preferable to repeating topology and activation checks during millions of simulated hardware cycles.

The chips remain authoritative for their internal state and outputs. The motherboard only routes signals and decides when a real chip package can possibly need evaluation.

## v0.84.0 — package-level compiled input masks

- Combines multiple changed package pins into one pending 64-bit input mask.
- Avoids repeating activation-predicate and queue checks after a package is already scheduled.
- Lets compiled chip packages receive one input-change event for all changes accumulated since their previous evaluation.
- Keeps every individual pin level and revision observable.
- Keeps clock-edge handling, electrical resolution, contention and chip behavior unchanged.
- Uses the existing generic `Evaluate()` path for modules that have not adopted the optional compiled-input contract.
- Rebuilds all package pin masks automatically when the topology changes.

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

- motherboard-owned signal routing;
- chip-published activation contracts;
- package-level changed-input and output masks;
- topology-derived dependency routes;
- dense pin/net/component indexes;
- packed buses;
- explicit sequential boundaries;
- validation between generic and compiled execution.

The Famicom is the first demanding benchmark, not a special case inside the generic engine.

## License

MIT. See [LICENSE](LICENSE).
