# AxetosOS Products / NES

AxetosOS Products / NES is a physical virtual-hardware implementation of Nintendo/Famicom-class hardware built on the AxetosOS hardware platform.

The project models the machine as a motherboard populated by independent chip packages connected through physical pins, buses, traces and clocks. The same hardware description is then compiled into an efficient executable circuit before normal gameplay.

The long-term goal is larger than NES emulation: the compiler and electrical/runtime layers are being designed as **generic virtual-hardware infrastructure** so future machines can be built from different motherboards and chips without adding product-specific knowledge to the compiler.

## Current release

**v2.33.0**

The validated v2.32.0 baseline is:

- **332 / 332 tests passing**;
- physical Controller 1 input confirmed in real Super Mario Bros. gameplay;
- normal paced NROM/MMC1/MMC3 execution holds approximately 60 FPS on the development machine;
- true uncapped generic whole-circuit throughput is approximately **152 FPS / 2.54x NTSC real time** for NROM and MMC1;
- Mapper-2/UxROM and Mapper-3/CNROM synthetic hardware smoke machines exceed **5x NTSC real time**;
- Mapper 4 / MMC3 synthetic hardware passes, and real Super Mario Bros. 2 and Super Mario Bros. 3 workloads exercise sustained PRG/CHR banking; Super Mario Bros. 3 produced more than five thousand cartridge IRQ assertions during the validation run.

v2.33.0 adds Mapper 7 / AxROM as replaceable cartridge hardware: one switchable 32 KiB PRG window, an end-of-M2 board latch, 8 KiB CHR RAM, mapper-controlled single-screen CIRAM page selection, no PRG RAM/IRQ, and NES 2.0 submapper-controlled bus-conflict behavior. The legacy/default Mapper-7 path follows the established no-bus-conflict convention while submapper 2 enables AND-style conflicts.

CIRAM A10 is deliberately a **live cartridge-owned combinational output** because AxROM bit 4 changes it at runtime; only /CIRAM-CE and the inactive IRQ output are eligible for static topology folding. No generic compiler or motherboard semantics were added for AxROM. v2.33.0 awaits local Release-suite and smoke-ROM validation; the expected suite is **346 tests**.

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

Implemented areas include:

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
- Mapper 2 / UxROM cartridges with switchable PRG, CHR RAM and fixed mirroring;
- Mapper 3 / CNROM cartridges with fixed PRG, switchable CHR ROM and fixed mirroring;
- Mapper 4 / MMC3-family cartridges with PRG/CHR banking, live mirroring, optional RAM and cartridge IRQ circuitry;
- Mapper 7 / AxROM cartridges with switchable 32 KiB PRG, CHR RAM and live single-screen CIRAM selection;
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

`--uncapped` is a compute-headroom benchmark, not fast-forward audio playback. The complete virtual video/audio generation path remains active, but physical WaveOut submission is disabled and native window presentation is limited to 60 Hz so real-time host devices cannot cap the measured virtual-machine throughput.

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

Near-term work now proceeds through the remaining tracks: validating each new mapper against real commercial cartridges, expanding additional discrete/ASIC boards, revisiting generic compiler headroom when profiling justifies it, and then building the desktop product shell. Planned host features include:

- ROM loading from the desktop UI;
- pause/reset/power-cycle controls;
- save state / load state;
- native menu and settings UI;
- additional cartridge hardware/mappers;
- broader board-region support.

Save-state and host UI features are intentionally outside the simulated motherboard. They operate on or around the virtual machine rather than becoming NES hardware behavior.

## License

MIT License. See [LICENSE](LICENSE).
