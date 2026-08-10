# AxetosOS Products / NES

AxetosOS Products / NES is a physical virtual-hardware implementation of Nintendo/Famicom-class hardware built on the AxetosOS hardware platform.

The machine is modeled as a motherboard populated by independent chip packages connected through physical pins, buses, traces and clocks. The assembled hardware can then be compiled into an efficient executable circuit without teaching the compiler what an NES, CPU, PPU or mapper is.

The longer-term goal is generic virtual-hardware infrastructure that can also host other machines by supplying different motherboards, packages and physical topology.

## Current release

**v2.45.0**

Validated baseline before this release: **542 / 542 Release tests passing through v2.44.0**.

v2.45.0 adds Mapper 18 / Jaleco SS88006 cartridge hardware, including split-nibble PRG/CHR banking, optional protected work RAM, four-mode CIRAM routing and the ASIC's selectable-width CPU-cycle IRQ counter.

## Architecture

### Physical machine first

```text
Motherboard
  -> chip packages
  -> package pins
  -> physical traces / buses / clocks
  -> cartridge connector
  -> replaceable cartridge hardware
```

The motherboard is intentionally dumb. It transports signal levels and topology; it does not know CPU, PPU, mapper, register or game semantics.

Chip-owned logic decides what received pin levels mean and whether internal circuitry activates.

### Generic whole-circuit compiler

Normal execution can compile the assembled hardware before power-on. Compiler decisions must be derivable from generic hardware facets and physical topology only.

Valid generic optimizations include:

- fixed physical routes;
- state-independent combinational outputs;
- pre-resolvable bus targets;
- fixed address projections;
- immutable clock routing;
- static package connections;
- reusable delegates and dispatch paths.

The compiler must **not** contain NES-, mapper-, CPU-, PPU- or game-specific shortcuts. MMC, VRC, Namco, Bandai and other cartridge semantics remain inside their hardware components.

### Replaceable cartridges remain hardware

Compiled execution preserves the cartridge boundary:

```text
compiled motherboard runtime unit
        <-> physical cartridge boundary <->
compiled replaceable cartridge runtime unit
```

ROM, RAM, mapper ASICs, latches, bus conflicts, IRQ circuitry and board-local devices therefore remain owned by the cartridge.

## Current hardware scope

Core machine hardware includes:

- Famicom/NES motherboard topology;
- RP2A03 CPU/APU package behavior;
- RP2C02 PPU package behavior;
- work RAM, CIRAM and discrete support logic;
- physical CPU and PPU buses;
- native framebuffer presentation;
- native PCM audio output;
- responsive ROM loading screen;
- iNES and NES 2.0 cartridge metadata handling;
- two standard controller packages with physical strobe/clock/data wiring;
- external controller-button stimulus connected through physical button contacts only.

Implemented cartridge mapper numbers:

- **0 — NROM**
- **1 — MMC1**
- **2 — UxROM**
- **3 — CNROM**
- **4 — MMC3/MMC6 family**
- **7 — AxROM**
- **9 — MMC2 / PxROM**
- **10 — MMC4 / FxROM**
- **11 — Color Dreams**
- **16 — Bandai FCG-1/2 / LZ93D50**
- **18 — Jaleco SS88006**
- **34 — BNROM and NINA-001/002**
- **66 — GxROM**
- **71 — Camerica/Codemasters**
- **79 — NINA-03/NINA-06**
- **206 — DxROM / Namco-108 family**
- **227 — address-latch multicart hardware**

Mapper 16 currently covers the modern NES 2.0 distinctions used by mapper 16 itself:

- submapper 4: FCG-1/2 register decode in `$6000-$7FFF`, direct IRQ counter programming, no EEPROM;
- submapper 5: LZ93D50 register decode in `$8000-$FFFF`, latched CPU-cycle IRQ counter, optional 256-byte 24C02 serial EEPROM;
- submapper 0: legacy compatibility response in both documented register ranges.

Deprecated Mapper-16 submappers that represent materially different fitted hardware are intentionally left to their dedicated mapper numbers (153, 157 and 159) rather than approximated as Mapper 16.

Mapper 18 / Jaleco SS88006 includes:

- three switchable 8 KiB PRG windows plus a fixed final bank;
- eight independently switchable 1 KiB CHR windows;
- optional 8 KiB work RAM with read/write protection state;
- horizontal, vertical and both single-screen CIRAM routes;
- 4-, 8-, 12- and 16-bit masked CPU-cycle IRQ counting;
- the SS88006 external-sample control output as board state. Optional uPD7755C/uPD7756C sample synthesis remains separate cartridge hardware and is not fabricated from missing sample data.

## Build and test

Requirements:

- .NET 8 SDK
- Windows for the current native desktop host

Run the complete Release test suite:

```powershell
dotnet test -c Release
```

## Run a ROM

Launch with a ROM path:

```powershell
dotnet run -c Release --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost -- "C:\ROMs\game.nes" --board famicom
```

Or launch without a path to use the native ROM picker:

```powershell
dotnet run -c Release --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost
```

Controller 1 keyboard bindings:

- arrows — D-pad
- Z — A
- X — B
- Enter — Start
- Right Shift — Select
- Escape — exit

## Diagnostic execution modes

The production path automatically uses compiled physical execution when supported by the assembled machine.

Force the generic whole-circuit compiler:

```powershell
dotnet run -c Release --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost -- "C:\ROMs\game.nes" --board famicom --compiled-lab
```

Run the uncompiled physical propagation reference path:

```powershell
dotnet run -c Release --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost -- "C:\ROMs\game.nes" --board famicom --raw-hardware
```

`--reference-runtime` remains an alias for the raw diagnostic path.

`--uncapped` remains available for throughput/profiling diagnostics. It does not change emulated hardware semantics and should not be confused with normal hardware-paced gameplay.

## Runtime diagnostics

When the desktop host exits it reports reproducible hardware state such as:

- master clock and CPU instruction counts;
- CPU state and bus address;
- boot/reset/vblank/NMI checks;
- APU cycle and DAC activity;
- PPU raster, pipeline and fetch counters;
- controller latch/shift activity;
- mapper registers, bank state, IRQ state and cartridge bus counters where applicable.

These diagnostics are intended to expose hardware behavior without moving semantics into the motherboard or compiler.

## Repository layout

```text
src/Products/NES/
  AxetosOS.Products.NES.Abstractions/
  AxetosOS.Products.NES.Cartridges/
  AxetosOS.Products.NES.DesktopHost/
  AxetosOS.Products.NES.VirtualHardware/

hardware/
  boards/
  schemas/

samples/

tests/
  AxetosOS.Products.NES.Tests/
```

`AxetosOS.Products.NES.VirtualHardware` contains the electrical/runtime infrastructure together with the current motherboard, chip and cartridge models. Generic compiler/electrical functionality should remain independent of NES product semantics.

## Development principles

1. **Physical boundaries are authoritative.** Chips communicate through package interfaces unless communication is genuinely internal to one chip.
2. **Motherboards transport signals; chips own semantics.** No receiver-aware motherboard shortcuts.
3. **Compile topology, not product knowledge.** Optimizations must be derivable from generic hardware facets and connections.
4. **External hardware stays replaceable.** Cartridge hardware is not silently absorbed into motherboard logic.
5. **Validate hardware changes.** New cartridge families receive conformance tests plus real-ROM validation where practical.
6. **Host features stay outside the simulated motherboard.** ROM selection, UI, save states and settings operate around the machine rather than becoming NES circuitry.

## Direction

The current mapper-completion tranche is focused on major remaining hardware families rather than exhaustive mapper-number coverage. After Jaleco SS88006, the remaining planned tranche focuses on Sunsoft, Konami VRC, Namco and MMC5 hardware.

After that tranche, development returns to the desktop product shell and broader system features, including:

- in-window ROM loading;
- pause/reset/power-cycle controls;
- save state / load state;
- native menus and settings;
- controller configuration;
- broader compatibility testing and targeted mapper additions where real software requires them.

## License

MIT License. See [LICENSE](LICENSE).
