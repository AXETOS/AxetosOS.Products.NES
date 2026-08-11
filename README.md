# AxetosOS.Products.NES

`AxetosOS.Products.NES` is a reusable NES/Famicom virtual-hardware implementation for .NET 8.

The project models the machine as physical hardware: motherboards, chip packages, package pins, buses, traces, clocks, cartridge connectors and replaceable cartridge hardware. The assembled circuit can then be compiled into a faster executable representation without teaching the compiler what an NES, CPU, PPU or mapper is.

The same virtual-hardware architecture is intended to support other machines by supplying different motherboards, packages and physical topology.

## Design principles

### Physical boundaries are authoritative

Communication that crosses a package boundary crosses physical pins and nets. Logic that belongs inside one IC remains internal to that IC.

### The motherboard is deliberately dumb

The motherboard transports electrical state and defines topology. It does not contain CPU, PPU, mapper, register or game semantics.

Chip-owned logic decides what received signal levels mean and whether internal circuitry activates.

### Compilation is topology-driven

The runtime may compile an assembled machine into efficient execution paths, but every optimization must be derivable from generic hardware characteristics and physical connections.

Valid optimizations include fixed routes, immutable clock routing, static package connections, state-independent combinational outputs and pre-resolvable bus targets.

The compiler must not contain NES-, mapper-, CPU-, PPU- or game-specific shortcuts.

### Cartridges remain replaceable hardware

ROM, RAM, mapper ASICs, latches, IRQ circuitry, bus conflicts and cartridge-local devices remain on the cartridge side of the connector even when execution is compiled.

```text
compiled motherboard
        <-> physical cartridge boundary <->
compiled cartridge hardware
```

## Hardware scope

The current implementation includes:

- Famicom, NTSC NES and PAL NES machine variants;
- RP2A03 CPU/APU behavior;
- RP2C02 PPU behavior;
- work RAM, CIRAM and supporting discrete hardware;
- physical CPU and PPU buses;
- cartridge insertion and iNES/NES 2.0 metadata handling;
- standard controllers using physical strobe/clock/data wiring;
- framebuffer and audio output contracts for host applications;
- in-memory whole-machine capture/restore for fast checkpoints;
- portable whole-machine capture/restore for host-managed persistent save states;
- generic compiled execution plus a raw physical-propagation reference path.

## Supported cartridge mappers

Mapper definitions are described in `hardware/mapper-catalog.json` and the board files under `hardware/boards/`.

| Mapper | Hardware family |
|---:|---|
| 0 | NROM |
| 1 | MMC1 |
| 2 | UxROM |
| 3 | CNROM |
| 4 | MMC3 / MMC6 |
| 5 | Nintendo MMC5 |
| 7 | AxROM |
| 9 | MMC2 / PxROM |
| 10 | MMC4 / FxROM |
| 11 | Color Dreams |
| 16 | Bandai FCG-1/2 / LZ93D50 |
| 18 | Jaleco SS88006 |
| 19 | Namcot 163 |
| 21 | Konami VRC4a / VRC4c |
| 23 | Konami VRC4e / VRC4f |
| 24 | Konami VRC6a |
| 25 | Konami VRC4b / VRC4d |
| 26 | Konami VRC6b |
| 34 | BNROM / NINA-001/002 |
| 66 | GxROM |
| 69 | Sunsoft FME-7 / 5A / 5B family |
| 71 | Camerica |
| 79 | NINA-03 / NINA-06 |
| 85 | Konami VRC7 |
| 206 | DxROM / Namco-108 family |
| 227 | Address-latch multicart hardware |

Mapper behavior belongs to cartridge hardware. Adding another mapper should normally mean adding or extending cartridge components and board topology rather than adding mapper knowledge to the motherboard or compiler.

## Host integration

`VirtualNesBootHost` is the main host-facing machine harness. It loads a ROM into the virtual cartridge hardware, advances the physical machine clock and exposes output through host-facing video and audio sinks.

Important host-facing state operations include:

- `CaptureState()` / `RestoreState()` for fast same-process checkpoints;
- `CapturePortableState()` / `RestorePortableState()` for versioned cross-process state payloads that a host may persist to disk.

Portable state intentionally does not embed the ROM image. A host that persists a state is responsible for identifying and reloading the matching ROM before restoration.

External controller contacts remain host input rather than becoming frozen controller presses when a machine state is restored.

## Application boundary

This repository is the public NES hardware engine, not the AxetosOS desktop application.

Application features belong to the host around the machine, including:

- menus and window chrome;
- fullscreen behavior;
- ROM-library UX and file dialogs;
- pause/reset hotkeys;
- quick-save/load UI;
- persistent save-state files and save-state management;
- recording/export controls;
- application status bars and dialogs.

The NES framebuffer remains game output only. Hosts should consume the framebuffer/audio output directly for recording or export so application chrome is never part of captured game output.

The `AxetosOS.Products.NES.DesktopHost` project in this repository is a reference/diagnostic host used for hardware validation. It is not the AxetosOS product shell.

## Build and test

The hardware libraries and tests target .NET 8.

Run the NES test project directly:

```powershell
dotnet test .\tests\AxetosOS.Products.NES.Tests\AxetosOS.Products.NES.Tests.csproj -c Release
```

The repository contains automated coverage for the electrical model, motherboard/chip boundaries, CPU/APU/PPU behavior, controllers, ROM loading, compiled execution and supported cartridge hardware.

## Reference host

When this repository is checked out inside the full AxetosOS source workspace, the Windows diagnostic host can be launched with a ROM path:

```powershell
dotnet run -c Release --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost -- "C:\ROMs\game.nes" --board famicom
```

Or without a path to use its ROM picker:

```powershell
dotnet run -c Release --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost
```

Diagnostic execution switches include:

```text
--compiled-lab       force generic compiled physical execution
--raw-hardware       use the uncompiled physical-propagation reference path
--reference-runtime  alias for the raw diagnostic path
--uncapped           remove normal presentation pacing for throughput/profiling
```

These switches change the execution or diagnostic path, not the emulated hardware semantics.

## Repository layout

```text
hardware/
  boards/                 cartridge-board definitions
  schemas/                board/catalog schemas
  mapper-catalog.json     supported mapper catalog

product/                  product metadata
samples/                  synthetic test/sample ROMs and related assets

src/Products/NES/
  AxetosOS.Products.NES.Abstractions/
  AxetosOS.Products.NES.Cartridges/
  AxetosOS.Products.NES.VirtualHardware/
  AxetosOS.Products.NES.DesktopHost/    reference/diagnostic host

tests/
  AxetosOS.Products.NES.Tests/
```

`AxetosOS.Products.NES.VirtualHardware` contains the electrical/runtime infrastructure together with the current motherboard, chip and cartridge models. Generic electrical/compiler infrastructure must remain independent of NES product semantics.

## Extending the hardware

When adding a mapper or other device:

1. model the device as cartridge or package-owned hardware;
2. connect it through the same physical boundaries used by the real machine;
3. keep motherboard and generic compiler code free of mapper-specific semantics;
4. add conformance coverage for the new hardware behavior;
5. validate against real software where practical.

## License

MIT License. See [LICENSE](LICENSE).
