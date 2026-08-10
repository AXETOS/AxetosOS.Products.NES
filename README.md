# AxetosOS Products / NES

AxetosOS Products / NES is a physical virtual-hardware implementation of Nintendo/Famicom-class hardware built on the AxetosOS hardware platform.

The project models the machine as a motherboard populated by independent chip packages connected through physical pins, buses, traces and clocks. The same hardware description is then compiled into an efficient executable circuit before normal gameplay.

The long-term goal is larger than NES emulation: the compiler and electrical/runtime layers are being designed as **generic virtual-hardware infrastructure** so future machines can be built from different motherboards and chips without adding product-specific knowledge to the compiler.

## Current release

**v2.28.0**

v2.28.0 adds physical host-controller input and is awaiting local Release-suite validation. The last validated v2.27.0 baseline was:

- **281 / 281 tests passing**
- Famicom/NROM normal compiled runtime: **61.80 FPS uncapped**
- Generic whole-circuit NROM compiler: **60.18 FPS uncapped**
- Generic whole-circuit MMC1 compiler: **60.11 FPS uncapped**

The v2.28.0 suite is expected to contain **286** test cases after the new controller cross-runtime coverage is included; this README does not claim that result until it is run on the development machine.

These FPS values are local throughput measurements from the current development machine and are not hardware requirements or guaranteed results on other hosts. Normal gameplay is paced to the emulated hardware clock; `--uncapped` is for throughput benchmarking.

## Architecture

### Physical machine first

A machine is assembled from real virtual-hardware boundaries:

```text
Motherboard
  -> chip packages
  -> package pins
  -> physical traces / buses / clocks
  -> cartridge connector
  -> replaceable cartridge hardware
```

The motherboard is intentionally dumb. It transports physical signal levels and topology; it does not know CPU, PPU, mapper, register or cartridge semantics.

Chip-owned logic decides what a received pin level means and whether internal circuitry activates.

### Generic whole-circuit compiler

Normal execution compiles the assembled machine before power-on. The compiler derives its execution plan from generic hardware facets and physical topology only.

It may optimize facts such as:

- fixed physical routes;
- state-independent combinational outputs;
- pre-resolvable bus targets;
- fixed address projections;
- immutable clock routing;
- static package connections;
- reusable delegates and dispatch paths.

It must **not** contain NES-, mapper-, CPU-, PPU- or game-specific shortcuts.

Knowledge such as MMC1 banking or RP2C02 behavior belongs inside those hardware components, never inside the generic compiler.

This is the architectural rule that allows the same compiler design to be reused later for other machines such as a C64 or PlayStation once their motherboards and chips are modeled.

### Replaceable cartridges remain physical hardware

Compiled execution does not fuse the cartridge away as a software abstraction.

The current generic runtime retains:

```text
compiled motherboard runtime unit
        <-> physical cartridge boundary <->
compiled replaceable cartridge runtime unit
```

Mapper and ROM behavior therefore remain owned by the cartridge hardware.

## Current hardware scope

Implemented and actively validated areas include:

- Famicom/NES motherboard topology;
- RP2A03 CPU/APU package behavior;
- RP2C02 PPU package behavior;
- work RAM, CIRAM and discrete support logic;
- physical CPU and PPU buses;
- native framebuffer presentation;
- native PCM audio output;
- cartridge loading and iNES/NES 2.0 metadata handling;
- Mapper 0 / NROM cartridges;
- Mapper 1 / MMC1 cartridges;
- generic startup whole-circuit compilation;
- specialized fused NROM execution retained for comparison/performance validation;
- two standard controller packages with physical strobe/clock/data wiring;
- generic external controller-button stimulus connected only through physical button traces;
- desktop Controller 1 keyboard adapter: arrows=D-pad, Z=A, X=B, Enter=Start and Right Shift=Select.

The host adapter does not write `$4016/$4017`, CPU state, controller shift registers or game memory. It changes only external button-contact signal sources; the controller package and normal console circuitry perform latching and serial reads. Controller 2 has an independent physical host-input source/API and is ready for a later second-player binding.

## Build and test

Requirements:

- .NET 8 SDK
- Windows for the current native desktop host

Run the complete Release test suite:

```powershell
dotnet test -c Release
```

## Run a ROM

Normal execution automatically selects the production compiled path supported by the assembled machine.

### Famicom / normal paced execution

```powershell
dotnet run -c Release --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost -- "C:\ROMs\game.nes" --board famicom
```

### Uncapped throughput benchmark

```powershell
dotnet run -c Release --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost -- "C:\ROMs\game.nes" --board famicom --uncapped
```

### Force the generic whole-circuit compiler

Useful for A/B testing against a specialized compiled path:

```powershell
dotnet run -c Release --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost -- "C:\ROMs\game.nes" --board famicom --compiled-lab --uncapped
```

### Raw physical propagation diagnostic mode

The uncompiled runtime is retained for diagnostics/reference testing, not normal gameplay:

```powershell
dotnet run -c Release --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost -- "C:\ROMs\game.nes" --board famicom --raw-hardware --uncapped
```

`--reference-runtime` remains an alias for the raw diagnostic path.

## Runtime diagnostics

When the desktop host exits it prints hardware/runtime diagnostics including:

- master clock count;
- CPU instruction count and current state;
- rendered frame count and uncapped FPS statistics;
- boot/reset/NMI checks;
- APU cycle/DAC information;
- PPU raster, pipeline and fetch counters;
- mapper diagnostics where applicable.

These counters are intended to make hardware and performance regressions reproducible without adding product semantics to the motherboard or compiler.

## Repository layout

```text
src/Products/NES/
  AxetosOS.Products.NES.Abstractions/
  AxetosOS.Products.NES.Cartridges/
  AxetosOS.Products.NES.DesktopHost/
  AxetosOS.Products.NES.VirtualHardware/

tests/
  AxetosOS.Products.NES.Tests/
```

`AxetosOS.Products.NES.VirtualHardware` contains the generic electrical/runtime infrastructure together with the current motherboard and chip models. Reusable hardware/compiler functionality should remain independent of NES product semantics wherever possible.

## Development principles

1. **Physical boundaries are authoritative.** Chips communicate through their actual package interfaces unless the communication is genuinely internal to one chip.
2. **Motherboards transport signals; chips own semantics.** No receiver-aware motherboard shortcuts.
3. **Compile topology, not product knowledge.** Compiler optimizations must be derivable from generic hardware facets and connections.
4. **External hardware stays replaceable.** Cartridge hardware is not silently absorbed into motherboard logic.
5. **Correctness before benchmark claims.** Performance changes are validated against the test suite and real ROM runs.
6. **Uncapped speed is headroom.** Normal gameplay remains synchronized to physical hardware timing.

## Direction

Near-term work now proceeds through the remaining tracks: increasing generic compiled-runtime headroom while preserving electrical/package behavior, expanding cartridge hardware, and then building the desktop product shell. Planned host features include:

- ROM loading from the desktop UI;
- pause/reset/power-cycle controls;
- save state / load state;
- native menu and settings UI;
- additional cartridge hardware/mappers;
- broader board-region support.

Save-state and host UI features are intentionally outside the simulated motherboard. They operate on or around the virtual machine rather than becoming NES hardware behavior.

## License

MIT License. See [LICENSE](LICENSE).
