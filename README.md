# AxetosOS Products / NES

AxetosOS Products / NES is a physical virtual-hardware implementation of Nintendo/Famicom-class hardware built on the AxetosOS hardware platform.

The project models the machine as a motherboard populated by independent chip packages connected through physical pins, buses, traces and clocks. The same hardware description is then compiled into an efficient executable circuit before normal gameplay.

The long-term goal is larger than NES emulation: the compiler and electrical/runtime layers are being designed as **generic virtual-hardware infrastructure** so future machines can be built from different motherboards and chips without adding product-specific knowledge to the compiler.

## Current release

**v2.38.0**

The validated v2.37.1 hardware baseline is:

- **390 / 390 tests passing**;
- physical Controller 1 input confirmed in real Super Mario Bros. gameplay;
- normal paced NROM/MMC1/MMC3/AxROM execution holds approximately 60 FPS on the development machine;
- true uncapped generic whole-circuit throughput is approximately **152 FPS / 2.54x NTSC real time** for NROM and MMC1;
- Mapper-2/UxROM and Mapper-3/CNROM synthetic hardware smoke machines exceed **5x NTSC real time**;
- Mapper 4 / MMC3 is real-game validated by sustained Super Mario Bros. 2 and Super Mario Bros. 3 workloads, including thousands of real cartridge IRQ assertions;
- Mapper 7 / AxROM is real-game validated by Battletoads for 11,600 frames, with sustained mapper writes, cartridge PPU traffic and CHR-RAM writes at real-time pacing.
- Mapper 11 / Color Dreams is real-game validated by Metal Fighter for 4,523 frames at approximately 60.15 FPS, including thousands of mapper writes and sustained cartridge PPU traffic;
- Mapper 66 / GxROM is real-game validated by Thunder & Lightning for 9,215 frames at approximately 60.13 FPS, including 15,707 mapper writes and sustained cartridge PPU traffic.
- Mapper 71 / Camerica-Codemasters is real-game validated by The Fantastic Adventures of Dizzy for 11,593 frames at approximately 60.15 FPS, including more than 412,000 mapper writes and sustained cartridge PPU/CHR-RAM traffic.

v2.34.0 adds Mapper 11 / Color Dreams as replaceable cartridge hardware: one switchable 32 KiB PRG-ROM window, one switchable 8 KiB CHR-ROM window, a shared 8-bit end-of-M2 latch, fixed H/V CIRAM wiring, no PRG RAM/IRQ, and standard AND-style CPU/ROM bus conflicts. Register D0-D1 select PRG, D4-D7 select CHR, while D2-D3 remain latch outputs associated with the original board's lockout-defeat circuitry rather than memory banking.

No generic compiler or motherboard semantics were added for Mapper 11. v2.34.1 is validated locally at **359 / 359 tests**; its Color Dreams smoke ROM selects PRG bank 1 and CHR bank 3 correctly at approximately **305.7 FPS / 5.09x NTSC real time** on the development machine.

v2.35.0 improves desktop startup UX without changing virtual-hardware behavior. The native game window is created immediately after ROM selection and displays an animated **Loading ROM** screen while ROM parsing, physical machine assembly and startup compilation run on a worker thread. The Win32 message pump remains active during loading, Escape/window-close remains responsive, the same presenter is reused for gameplay, and startup diagnostics now report total ROM parse + assembly + compilation time. The loading screen has been confirmed in local desktop use.

v2.36.0 adds Mapper 66 / GxROM as replaceable physical cartridge hardware. Standard GNROM/MHROM wiring uses a four-bit latch with CPU D4-D5 selecting up to four 32 KiB PRG-ROM banks and D0-D1 selecting up to four 8 KiB CHR-ROM banks, fixed H/V CIRAM wiring, no cartridge RAM/IRQ, and standard AND-style CPU/ROM bus conflicts. It is locally validated at **372 / 372 tests** and by Thunder & Lightning sustained gameplay.

v2.37.1 retains the Mapper 71 / Camerica-Codemasters physical cartridge introduced in v2.37.0 and corrects its no-bus-conflict test fixture. It provides a switchable 16 KiB PRG window at `$8000-$BFFF`, a fixed-last 16 KiB window at `$C000-$FFFF`, 8 KiB CHR RAM, no PRG RAM/IRQ or CPU/ROM bus conflicts, and NES 2.0 submapper-aware nametable wiring. Submapper 0 retains hardwired horizontal/vertical mirroring; submapper 1 models the BF9097/Fire Hawk board whose bit-4 mirroring latch drives CIRAM A10 directly. The board-local CIC-stun latch decode is retained as diagnostic state pending a future normalized CIC cartridge connector. No generic compiler or motherboard semantics are added. v2.37.1 is locally validated at **390 / 390 tests** and by The Fantastic Adventures of Dizzy for 11,593 frames at approximately 60.15 FPS, with more than 412,000 mapper writes and sustained cartridge PPU/CHR-RAM traffic.

v2.38.0 adds Mapper 79 / NINA-03/NINA-06 as replaceable physical cartridge hardware. The board exposes a switchable 32 KiB PRG-ROM window (up to 64 KiB total), a switchable 8 KiB CHR-ROM window (up to 64 KiB total), fixed H/V CIRAM wiring, no cartridge RAM/IRQ and no CPU/ROM bus conflicts. Its low-address control latch is decoded from the physical CPU connector condition `010x xxx1 xxxx xxxx`: CPU D3 selects PRG and D0-D2 select CHR. The generic compiler receives those address/control requirements only as ordinary package-pin conditions; it contains no mapper-79 semantics. The expected Release suite is **413 tests** pending local validation.

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
- native framebuffer presentation with responsive startup loading screen;
- native PCM audio output;
- cartridge loading and iNES/NES 2.0 metadata handling;
- Mapper 0 / NROM cartridges;
- Mapper 1 / MMC1 cartridges;
- Mapper 2 / UxROM cartridges with switchable PRG, CHR RAM and fixed mirroring;
- Mapper 3 / CNROM cartridges with fixed PRG, switchable CHR ROM and fixed mirroring;
- Mapper 4 / MMC3-family cartridges with PRG/CHR banking, live mirroring, optional RAM and cartridge IRQ circuitry;
- Mapper 7 / AxROM cartridges with switchable 32 KiB PRG, CHR RAM and live single-screen CIRAM selection;
- Mapper 11 / Color Dreams cartridges with switchable 32 KiB PRG, switchable 8 KiB CHR ROM, fixed mirroring and board-local bus conflicts;
- Mapper 66 / GxROM cartridges with switchable 32 KiB PRG, switchable 8 KiB CHR ROM, fixed mirroring and standard board-local bus conflicts;
- Mapper 71 / Camerica-Codemasters cartridges with switchable 16 KiB PRG, fixed-last PRG, CHR RAM, no bus conflicts and optional live Fire Hawk single-screen CIRAM selection;
- Mapper 79 / NINA-03/NINA-06 cartridges with switchable 32 KiB PRG, switchable 8 KiB CHR ROM, low-address decoded control latch, fixed mirroring and no bus conflicts;
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
