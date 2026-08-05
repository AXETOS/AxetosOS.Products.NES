# AxetosOS Products / NES

[![Status](https://img.shields.io/badge/status-playable-brightgreen)](#playable-nes-emulator)
[![Playable emulator](https://img.shields.io/badge/playable_emulator-v0.23.0-blue)](#playable-nes-emulator)
[![VirtualHardware](https://img.shields.io/badge/virtualhardware-v0.50.0-blueviolet)](#virtualhardware-nes)
[![Platform](https://img.shields.io/badge/platform-AxetosOS-informational)](#axetosos-native-product)
[![Language](https://img.shields.io/badge/language-C%23-512BD4)](#technology)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)

> This repository contains two NES implementations: the established playable emulator and a new independent VirtualHardware implementation that reconstructs the machine from electrically connected, reusable components.

## Project status

The repository now has two separately versioned development tracks. The playable emulator remains the stable reference implementation while VirtualHardware advances independently toward a complete pin-driven NES.

### Playable NES emulator

**Current stable version: v0.23.0**

Supported cartridge images run in an AxetosOS-owned native desktop window with keyboard input, PCM audio, automatic console timing selection and live performance diagnostics. This implementation remains intact and acts as the working compatibility oracle for the independent hardware simulation.

Current highlights:

- modular CPU, PPU, APU, bus, controller and cartridge hardware;
- all documented RP2A03 CPU opcodes and addressing modes;
- cycle-timed CPU data-bus reads and writes for synchronized PPU, APU, mapper and DMA side effects;
- NTSC 2C02 odd-frame timing, including the one-clock pre-render skip only while rendering is enabled;
- RP2C02 OAM bus behavior during rendering, sprite-fetch OAMADDR reset, evaluator start-address rotation and revision-specific OAM refresh corruption;
- RP2C02 CPU-facing I/O data-bus latch behavior, including write-only register reads, PPUSTATUS low-bit retention and buffered palette reads;
- background rendering and a dot-clocked RP2C02 sprite pipeline with secondary OAM, eight output units, 8×8/8×16 fetches and sprite-zero identity;
- native desktop video, audio and keyboard input;
- broad cartridge support across Nintendo, discrete and common third-party board families;
- battery-backed MMC1 save RAM persistence;
- automatic NTSC, PAL and Dendy timing selection;
- data-driven cartridge-board definitions;
- headless diagnostics, framebuffer export and WAV export;
- native ROM selection dialog;
- automated hardware and compatibility tests.

### VirtualHardware NES


### External background and sprite fetch bus (v0.37.0)

The clocked RP2C02 background and sprite pipelines now fetch nametable, attribute, CHR pattern and palette bytes through the motherboard-owned PPU address/data nets and active-low `/RD` strobe. Visible pixels are assembled by a settling fetch microsequence against the independent CHR/CIRAM/palette device; the renderer no longer reads its compatibility VRAM array directly.

### Standalone NTSC motherboard IC packages (v0.39.0)

The chip-first reconstruction now includes independent, unwired package models for the SN74LS139A dual decoder, SN74LS373 octal transparent latch, SN74LS368A hex inverting tri-state driver, and HM6116-compatible 2K x 8 SRAM. Each package exposes its own power, input, output, address, data, and control pins and is tested independently. No NES motherboard composition or cross-chip calls are introduced in this milestone.

### RP2C02 external PPU bus integration (v0.36.0)

The CPU-facing RP2C02 register package now owns a fourteen-bit PPU address bus, bidirectional eight-bit data bus, and active-low read/write strobes. Motherboard wiring connects those pins to the independent CHR/CIRAM/palette component introduced in v0.35.0. CPU PPUDATA accesses therefore appear as real electrical PPU-memory transactions, with diagnostics counting external reads and writes. The existing clocked renderer remains compatible while the next milestone replaces its consolidated fetch storage with a per-dot bus sequencer.

### Pin-driven NROM PPU memory (v0.35.0)

The ROM factory now carries CHR data and cartridge mirroring into the selected motherboard. A new pin-driven PPU memory component exposes a fourteen-bit address bus, bidirectional data bus and active-low read/write strobes. It provides 8 KiB CHR ROM or CHR RAM, CIRAM with horizontal, vertical or four-screen wiring, palette RAM mirroring and write protection for cartridge CHR ROM. This is the electrical memory foundation that the RP2C02 fetch pipeline will consume in the next integration stage.

### Automatic ROM-to-motherboard selection (v0.34.0)

The independent VirtualHardware launch path now parses iNES and NES 2.0 cartridge metadata, resolves `Auto`, `NTSC-U`, `NTSC-J`, or `PAL`, and constructs the corresponding physical motherboard profile. Selection priority is explicit override, reliable header timing, filename refinement/fallback, then NTSC-U. The motherboard never inspects ROM filenames or headers itself. Current launch validation intentionally accepts NROM mapper 0 only while later cartridge boards remain unwired.


**Current development version: v0.39.0**

VirtualHardware is an independent electrical simulation. Components react only to power, clocks, pin levels, connected nets and their own internal state. The motherboard owns all wiring, and no execution is delegated to the playable emulator's CPU, PPU or APU classes.

Current foundation:

- digital pins, nets, drive strengths, contention, buses, rails and pull resistors;
- reusable logic, memory, clock, reset and instrumentation components;
- pin-driven MOS 6502 with official opcode decoding, interrupts, stack cycles and external bus traffic;
- NES CPU motherboard slice with mirrored work RAM and NROM PRG mapping;
- pin-driven controller I/O at `$4016/$4017`;
- CPU-facing RP2C02 register interface, VRAM, OAM and buffered PPUDATA behavior;
- clocked NTSC PPU timing, vblank and open-drain NMI generation;
- background-rendering pipeline with nametable, attribute, pattern-plane, palette, scrolling and pixel-output behavior;
- first sprite-rendering pipeline with eight-entry secondary OAM, sprite pattern fetches, transparency, priority, flips, overflow and sprite-zero hit behavior.

VirtualHardware is not yet a replacement for the playable emulator. It is the active hardware-reconstruction track and will eventually produce a complete NES through component behavior and motherboard wiring alone.

## Run

Requirements:

- Windows 10 or later;
- .NET 8 SDK;
- AxetosOS Core source available in the parent AxetosOS workspace.

Build and test:

```powershell
dotnet build
dotnet test
```

Open the native ROM picker:

```powershell
dotnet run --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost
```

Open a cartridge image directly:

```powershell
dotnet run --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost -- "C:\ROMs\game.nes"
```

Override automatic timing selection when required:

```powershell
dotnet run --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost -- "C:\ROMs\game.nes" --timing pal
```

Accepted timing values are `auto`, `ntsc`, `pal`, and `dendy`. Auto mode prefers NES 2.0 metadata, then legacy header information and standard filename region tags, and otherwise defaults to NTSC.

## Controls

| Key | NES control |
|---|---|
| Arrow keys | D-pad |
| Z | A |
| X | B |
| Enter | Start |
| Right Shift | Select |
| Right Shift + Enter | Capture a diagnostic snapshot |
| Right Shift + Down | Start or stop buffered continuous diagnostics |
| Escape | Exit |

Diagnostic captures append CPU, PPU, CIRAM, and supported mapper state to a `.nes-diagnostics.log` file beside the loaded cartridge image. A matching framebuffer snapshot is written as a portable pixmap (`.ppm`) so the recorded hardware state can be correlated with the visible failure.

Continuous diagnostics are buffered entirely in memory while the emulator runs and written to CSV only when recording stops or the host closes. The rolling buffer retains the latest 120,000 frames, approximately 33 minutes at NTSC speed, avoiding per-frame disk I/O that could disturb host pacing.

## Supported cartridge hardware

| Mapper | Board family | Status |
|---:|---|---|
| 0 | NROM | Supported |
| 1 | MMC1 | Supported |
| 2 | UxROM | Supported |
| 3 | CNROM | Supported |
| 4 | MMC3 / MMC6 foundation | Supported |
| 7 | AxROM | Supported |
| 11 | Color Dreams | Supported |
| 66 | GxROM | Supported |
| 71 | Camerica | Supported |
| 79 | NINA-03 / NINA-06 | Supported |
| 206 | DxROM | Supported |

Additional cartridge boards will be added through the same modular board-definition system. Unsupported boards are reported through a native error dialog rather than terminating with an unhandled exception.

## AxetosOS-native product

This repository contains the public NES product. It is designed to run through AxetosOS rather than as an unrelated standalone emulator.

AxetosOS provides the reusable platform services used by the product, including:

- product discovery and lifecycle;
- module composition;
- native framebuffer presentation;
- native audio output;
- input integration;
- runtime scheduling;
- diagnostics and observability.

The NES product supplies the emulated console hardware, cartridge system and product-specific host integration.

## Architecture

The virtual console is composed from independently testable hardware modules:

```text
NES Console
├── RP2A03 processor package
│   ├── CPU
│   ├── APU
│   ├── DMA
│   └── controller I/O
├── RP2C02 PPU
├── CPU and PPU buses
├── work RAM and video memory
├── controller ports
└── cartridge connector
    └── cartridge board
```

Cartridge boards are selected from ROM metadata and assembled from reusable devices and board definitions. This keeps mapper behavior separate from the console itself.

## Desktop host

The desktop host provides:

- native ROM selection;
- integer-scaled 256×240 output;
- keyboard controller input;
- native PCM playback;
- live emulation speed, FPS and audio-buffer diagnostics;
- clean keyboard and window-close shutdown.

The host can also accept a ROM path directly for development, testing and automation.

## Headless host

The headless host supports deterministic execution without opening a desktop window. It can:

- inspect ROM metadata;
- execute a selected number of CPU cycles;
- apply scripted controller input;
- export framebuffer images;
- export generated audio;
- report CPU, PPU, APU and mapper diagnostics.

Repository-owned diagnostic ROMs and input scripts are available under `samples/`.

## Roadmap

Completed:

- [x] CPU instruction foundation
- [x] PPU background and sprite output
- [x] controller input and OAM DMA
- [x] pulse, triangle, noise and DMC audio
- [x] native desktop video and audio
- [x] NROM, MMC1, UxROM and broad discrete/MMC3-family mapper support
- [x] battery-backed save RAM persistence
- [x] NTSC, PAL and Dendy timing profiles
- [x] native unsupported-mapper reporting
- [x] full-speed execution on supported software
- [x] native ROM picker

Planned:

- [ ] additional licensed, expansion-audio and uncommon mapper families;
- [ ] further PPU timing accuracy;
- [ ] further APU accuracy and lower latency;
- [ ] save states;
- [ ] native gamepad support;
- [ ] fullscreen and presentation settings;
- [ ] expanded compatibility testing.




## v0.33.0 Regional VirtualHardware timing profiles

The VirtualHardware motherboard now selects an explicit physical console family instead of assuming one hard-coded NTSC machine:

- `NTSC-U` for North American NES hardware
- `NTSC-J` for Japanese Famicom/NES timing
- `PAL` for European and Australian NES hardware

A ROM image is loaded once; it is not triplicated. The selected motherboard profile supplies the CPU clock, PPU clock, CPU-to-PPU phase ratio, scanline count, vblank boundary and pre-render line. NTSC-U and NTSC-J intentionally remain distinct profiles even where their current timing values match, leaving room for later regional I/O, palette and peripheral differences.

PAL uses its non-integer 16:5 PPU-to-CPU half-cycle ratio through a phase accumulator rather than approximating it as 3:1. The timing core is now parameterized for both the 262-line NTSC raster and 312-line PAL raster.

## v0.32.0 VirtualHardware OAM DMA

The VirtualHardware motherboard now includes the RP2A03 `$4014` OAM-DMA path. A CPU write latches the source page, the DMA controller electrically requests the CPU bus, stalls and disconnects the processor outputs, performs 256 alternating source-memory reads and OAM writes, then releases the CPU to resume execution. The transfer honors `OAMADDR` wrapping, exposes diagnostic transfer/stall counters, and remains visible to the passive CPU bus analyzer. No RAM or PPU storage is accessed through software shortcuts.

## v0.31.0 VirtualHardware sprite pipeline

The independent RP2C02 now performs clock-driven sprite evaluation at the start of each visible scanline. Primary OAM is filtered into an eight-entry secondary OAM, with the ninth in-range sprite setting the overflow flag. The selected sprites fetch 8×8 or 8×16 pattern data from PPU memory, apply horizontal and vertical flipping, choose sprite palettes, and produce transparent or opaque sprite pixels. Sprite/background composition observes front/behind priority and raises sprite-zero hit when non-transparent sprite zero and background pixels overlap before dot 255. PPUSTATUS now exposes sprite overflow and sprite-zero hit alongside vblank. The framebuffer remains an inspection surface; rendering state is owned by the new VirtualHardware RP2C02 and does not call the playable emulator.

## Technology

- C#
- .NET 8
- AxetosOS Core
- native Windows platform APIs through AxetosOS services
- xUnit for automated tests

No third-party game engine or emulator frontend is used.

## ROMs and copyright

ROM files are not included in this repository and must not be committed.

Use only cartridge images you are legally entitled to use. Nintendo and NES are trademarks of Nintendo. This project is an independent technical project and is not affiliated with or endorsed by Nintendo.

## License

The public source in this repository is licensed under the [MIT License](LICENSE).


### Runtime diagnostics

Press **Right Shift + Enter** for a snapshot. Press **Right Shift + Down** to start or stop continuous diagnostic recording. Recording remains in memory while the product runs and is written beside the cartridge image only when recording stops or the host exits.

The recording contains a 120,000-frame state ring buffer plus a bounded critical-event stream covering MMC1 serial writes and register commits, sprite-zero hits, OAM address/data/DMA writes, sprite-zero scanline selection, compressed PPU-status polling activity, CPU stack state, and the most recent sprite-zero-hit position. This allows failures to be inspected across the exact transition without continuous disk I/O.

The critical event section records accepted and ignored MMC1 serial writes, register commits, sprite-zero hits, sprite-zero evaluation inputs, and compressed `$2002` polling bursts. Sprite-zero evaluation rows include OAM bytes, decoded pattern planes, sprite/background opacity masks, overlap masks, clipping state, scroll state, and an explicit rejection reason. This keeps the trace useful without writing every CPU instruction.


## RP2C02 sprite evaluation

The PPU now includes a dedicated dot-clocked sprite evaluator for primary and secondary OAM. It models secondary-OAM clearing on dots 1–64, odd/even OAM evaluation on dots 65–256, first-eight-sprite selection, sprite-zero identity propagation, and the RP2C02 diagonal sprite-overflow comparator behavior. This evaluator is the hardware foundation for the subsequent dot-timed sprite-pattern fetch and output-unit stage.


### RP2C02 mid-scanline OAM behavior

The PPU models the rendering-time `$2004` OAM data bus by phase: secondary-OAM clear drives `$FF`, primary-OAM evaluation exposes its read latch, and dots 257–320 expose the secondary-OAM byte used by the active sprite-fetch slot. Rendering enabled partway through the sprite-fetch interval joins the sequencer at the current dot and forces `OAMADDR` to zero as the hardware does.

- RP2C02 VBlank/PPUSTATUS race timing, including dot-boundary VBlank and NMI suppression

- RP2A03 seven-cycle IRQ/NMI/BRK bus microsequences with dummy reads, cycle-separated stack writes, vector fetches, and NMI vector hijacking.

## Virtual motherboard composition

The NES product now has an explicit `NesMotherboard` composition root. It owns the functional board wiring between the RP2A03 CPU/APU, RP2C02 PPU, CPU and PPU buses, work RAM, CIRAM, palette RAM, controller ports, DMA controller, IRQ combiner, cartridge board, signal lines, and master clock. Hosts can consume the assembled board instead of recreating those connections independently. This is the first migration step toward inspectable, reusable AxetosOS virtual-hardware components.

### Inspectable motherboard composition

The NES motherboard now publishes a machine-readable inventory of its functional
components and their explicit clock, bus, signal, DMA, and cartridge connections.
This is the first foundation for AxetosOS board visualization, signal tracing,
component replacement, and reuse of virtual-hardware building blocks in other
machines and simulations. Runtime behavior still comes from the connected chips;
the topology is descriptive and does not introduce game-specific outcomes.


### RP2A03 package composition

The motherboard now exposes the RP2A03 as an inspectable chip package containing
the validated CPU core, APU, controller I/O registers, DMA unit, and external
signal bundle. This establishes physical ownership without duplicating or replacing
the working execution path. Future chip extraction can therefore move one internal
functional block at a time while whole-machine behavior remains protected by the
existing integration tests.


### RP2C02 package boundary

The motherboard now exposes an inspectable `Rp2C02Package` built from the same live components used by emulation: the PPU timing/fetch/pixel core, dot-clocked sprite evaluator, internal palette RAM, and NMI output. This is an ownership and topology boundary only; it does not duplicate rendering or replace the validated runtime path.


### First-class memory devices

The live 2 KiB CPU work RAM, 2 KiB CIRAM, 32-byte palette RAM, 256-byte primary OAM, and 32-byte secondary OAM are now published as independently inspectable hardware components. Inspection reads the same storage used by the running machine; it does not create a parallel emulator state or alter bus timing.


The live CPU and PPU buses are also first-class inspectable hardware modules. AxetosOS can observe their address/data width, open-bus state, attached devices, and latest completed transaction without enabling a separate trace engine or changing bus routing.

- Controller ports are now first-class live hardware components, exposing each serial connector, the shared $4016 OUT0 strobe line, latched button state, shift-register state, and serial read count without changing the working input path.

### First-class RP2A03 DMA hardware

The live OAM and DMC DMA paths are exposed as separate inspectable channels connected through an explicit DMA bus-arbiter component. These views read the validated runtime state directly: source address, transfer phase, data latch, pending halt/alignment cycles, bus ownership, and RDY control. No alternate DMA implementation or game-specific timing path is introduced.

### RP2A03 APU internal components

The live APU now publishes its frame sequencer, both pulse channels, triangle channel, noise channel, and DMC channel as independently inspectable hardware blocks. These components expose the actual channel counters, timers, shift state, output level, and IRQ state used by the running emulator; no parallel audio implementation is introduced.

### RP2A03 CPU internal components

The live CPU now publishes an inspectable register file, execution-unit/microsequencer boundary, and interrupt controller. These are allocation-free views over the same validated CPU state and bus-cycle scheduler used by the running machine; they do not add a second instruction engine or alter opcode timing.


### RP2C02 rendering and NMI functional blocks

The live PPU now publishes its VRAM address/scroll unit, background fetch and shift pipeline, pixel compositor, and VBlank/NMI controller as independently inspectable hardware blocks. These components expose the same internal registers, latches, shifters, sprite-selection state, and NMI pin state used by the validated rendering path; they do not add a second renderer or precomputed video outcome.

### Cartridge boards as composed hardware

The live cartridge is now exposed as an inspectable board package containing its CPU-side PRG window, PPU-side CHR window, mapper/address-decoding logic, nametable-mirroring wiring, and IRQ output. These components wrap the same cartridge devices already attached to the CPU and PPU buses; they do not create a parallel mapper or memory path.


### Clock and signal network

The motherboard exposes the live master oscillator, CPU/PPU dividers, NMI, IRQ, RESET, RDY, and IRQ-combiner state as first-class hardware components. These are views over the same scheduler and signal lines used by the running console; no alternate timing path is introduced.

### Motherboard address decoding and cartridge connector

The live CPU and PPU buses now publish first-class address-decoder components and an explicit NES cartridge edge connector. These components expose the selected address region, data direction, responding device, and CPU/PPU cartridge chip-select state while the existing buses and mapper devices continue to execute all reads and writes. This preserves the validated emulator path and makes the motherboard wiring available to future AxetosOS board visualization and composition tools.

### First-class console I/O boundary

The motherboard topology now includes the physical console-facing boundary: controller sockets, composite video output, mono audio output, power switch, and reset button. These components expose the same live controller shift registers, RP2C02 framebuffer, APU sample buffer, and motherboard lifecycle paths already used by the running machine; they do not introduce a second host or rendering/audio implementation.

### Unified live hardware inspection

`NesMotherboard.Inspection` provides a stable component registry and an allocation-free point-in-time snapshot of the running console. AxetosOS can resolve any published component by module ID, enumerate the board connections, and observe clock counts, CPU execution state, PPU position, bus transactions, and external signal levels through one boundary. The registry indexes the existing live topology and does not own or duplicate emulation state.

## New pin-driven virtual hardware implementation

The validated emulator remains available as the working reference machine. A separate `AxetosOS.Products.NES.VirtualHardware` project now begins the true hardware-composition implementation.
- v0.26.0 adds a pin-wired NES CPU motherboard slice, NES RAM/PRG mapping and passive CPU-bus analysis.
- v0.25.0 expands the pin-driven MOS 6502 with indexed/indirect addressing, RMW cycles, shifts/rotates, BIT and RTI.

The new implementation starts below the NES level. Components communicate only through pins and resolved electrical nets. The first foundation includes digital levels, high-impedance outputs, weak and strong drivers, contention detection, power and ground rails, pull resistors, logic components, explicit board wiring, and a propagation-settling simulator.

The intended direction is that reusable chips own their internal behavior and react only to power, clocks, signals and data presented at their pins. A board owns the wiring. The NES will later be one board assembled from those independent components; the existing emulator remains untouched until the new machine reaches equivalent capability.

### VirtualHardware digital component library

The independent pin-driven simulator now includes reusable multi-bit buses,
tri-state buffers, rising-edge binary counters, one-of-N address decoders and
an asynchronous static RAM chip. These components know only their pins and
internal state. Address selection, data transfer, output enable and writes are
produced by resolved electrical nets rather than NES-specific method calls.

### Pin-driven virtual-hardware microcomputer

The independent `AxetosOS.Products.NES.VirtualHardware` implementation now includes a complete small computer assembled from reusable electrical and digital modules. Its processor, ROM, SRAM, decoder, oscillator, reset circuit, inverter, address bus and shared tri-state data bus communicate only through connected pins and resolved nets.

The demonstration processor drives address, data and read/write pins, samples memory data on clock edges, and has no direct reference to either memory chip. Address decoding selects RAM or ROM from the wiring, and the memory chips independently decide whether to drive, receive or release the shared data bus. This board is a proof of the architecture that will be used to construct the new pin-driven NES without modifying the validated reference emulator.

### Pin-driven 6502-family processor foundation

The separate `VirtualHardware` implementation now contains a reusable 6502-family processor boundary under `Components/Processors/Mos6502`. It exposes a 16-bit address bus, bidirectional 8-bit data bus, `R/W`, `SYNC`, `PHI2`, `/RESET`, `/IRQ`, `/NMI`, and `RDY` pins. Reset now executes as an explicit seven-cycle pin-driven sequence. IRQ and falling-edge-latched NMI entry perform observable stack writes and vector reads through the external buses, with hardware-interrupt status semantics and read-cycle-only `RDY` stalls. The processor has no RAM, ROM, motherboard, or NES-bus dependency. The executable subset intentionally remains small while the pin-level cycle engine is validated independently from the working emulator.



### External RP2C02 rendering fetch bus (v0.37.0)

The VirtualHardware background and sprite pixel pipelines now obtain nametable, attribute, CHR pattern and palette bytes through the motherboard-owned fourteen-bit PPU address bus, bidirectional data bus and active-low `/RD` strobe. Rendering no longer reads the RP2C02 register package's compatibility VRAM array. Each visible pixel is assembled by a settling fetch microsequence that drives and samples the same CHR/CIRAM/palette hardware used by CPU `PPUDATA` transactions.

The compatibility array remains temporarily limited to CPU-register inspection and buffered `PPUDATA` behavior while that final read-buffer path is migrated.

### VirtualHardware NES CPU motherboard slice (v0.26.0)

Version 0.26.0 moves the independent hardware simulator beyond isolated CPU fixtures. A reusable `NesCpuMotherboard` now composes the pin-driven MOS 6502, NTSC CPU oscillator, power-on reset circuit, control-line pull-ups, 2 KiB static work RAM, 32 KiB PRG ROM, address decoders and read-control inverter entirely through motherboard-owned nets. The board reproduces the NES internal RAM mirrors at `$0000-$1FFF` by leaving RAM address pins A11 and A12 physically unconnected, and supports NROM-128 16 KiB PRG mirroring into both `$8000-$BFFF` and `$C000-$FFFF` banks. A passive `Mos6502BusAnalyzer` observes resolved address, data, R/W, SYNC and PHI2 pins without direct component references and records external bus cycles for diagnostics and future compatibility comparison.

### VirtualHardware MOS 6502 execution core (v0.25.0)

The independent pin-driven MOS 6502 now includes a reusable execution core built entirely on external bus cycles. It includes the X and Y registers, status-flag handling, arithmetic and logic, register transfers, branches, stack operations and subroutine flow. Version 0.25.0 expands this foundation with zero-page indexed, absolute indexed, indexed-indirect and indirect-indexed addressing; NMOS-compatible indirect JMP page wrapping; accumulator and memory shifts/rotates; INC/DEC read-modify-write sequences with observable dummy and final writes; BIT; RTI; TSX; and TXS. The decoder now recognizes all 151 official opcode values, while opcode 0x00 intentionally remains a temporary test-program stop marker until a dedicated BRK sequence is introduced. Memory access remains visible only through A0-A15, D0-D7, R/W, SYNC, PHI2 and the control pins; it does not call the legacy emulator CPU or directly access a memory object.

## v0.27.0 VirtualHardware controller I/O slice

The independent pin-driven motherboard now includes an NES controller I/O package connected directly to the CPU address, data and R/W nets. Writes to `$4016` control the shared strobe, while reads from `$4016` and `$4017` shift the two independent eight-button registers in NES order: A, B, Select, Start, Up, Down, Left and Right. External button sources affect the package only through pins, and every controller access remains visible to the passive CPU bus analyzer.


## v0.30.0 VirtualHardware PPU register interface

The independent pin-wired NES motherboard now includes the CPU-facing RP2C02 register interface at $2000-$3FFF, including eight-register mirroring, PPUCTRL/PPUMASK, PPUSTATUS vblank clearing, OAMADDR/OAMDATA, PPUSCROLL/PPUADDR write-latch behavior, buffered PPUDATA reads, palette immediate reads, and 1/32 VRAM address increments. The component observes only bus pins and an external vblank signal; rendering remains outside this milestone.


### VirtualHardware v0.30.0 — clocked PPU timing and NMI foundation

The independent pin-driven motherboard now includes an NTSC RP2C02 timing core. It advances at three PPU cycles per CPU cycle, tracks all 341 dots across 262 scanlines, raises vblank at scanline 241 dot 1, and clears it at pre-render scanline 261 dot 1. `PPUCTRL` bit 7 is exported as an electrical NMI-enable signal. During vblank the PPU pulls the shared open-drain `/NMI` line low; otherwise the motherboard resistor pulls it high. The existing external vblank source remains available as a diagnostic force input and no legacy PPU implementation is invoked.


## v0.30.0 VirtualHardware background pipeline

The independent RP2C02 model now consumes clocked scanline and dot pins to fetch nametable, attribute, pattern and palette data, shifts out visible background pixels, and records a 256x240 inspection framebuffer without calling the legacy emulator PPU.

## v0.34.0 VirtualHardware ROM loading and automatic motherboard selection

The independent VirtualHardware launch boundary now reads iNES and NES 2.0 files without constructing any legacy emulator runtime object. In `Auto` mode it resolves the physical console in this order: explicit user override, NES 2.0 timing metadata, legacy iNES PAL hint, filename refinement/fallback, and finally NTSC-U. NTSC ROMs tagged as Japan construct an NTSC-J motherboard; PAL metadata constructs a PAL motherboard. The resolved region and selection source remain visible to the host for diagnostics.

The new factory currently validates NROM mapper 0 with 16/32 KiB PRG ROM and 0/8 KiB CHR ROM before constructing `NesCpuMotherboard`. Unsupported mappers fail explicitly instead of silently running through the playable emulator. This is the software composition layer: ROM metadata selects the motherboard, while the motherboard itself knows only the supplied physical hardware profile and cartridge bytes.

### VirtualHardware RP2C02 internal chip decomposition (v0.38.0)

The RP2C02 is now being decomposed into explicit pin-connected internal hardware rather than extending the earlier consolidated behavioral package. The first reusable chips are:

- `Rp2C02VramAddressRegisters`: physical `v`, `t`, fine-X and write-toggle state with scroll/address increment and transfer control pins;
- `Rp2C02DataBufferRegister`: edge-triggered PPUDATA read-buffer register;
- `Rp2C02BusSequencer`: CPU/render request arbitration and external PPU address/data/read/write bus sequencing.

These components contain no cartridge, CIRAM, palette or framebuffer arrays. They communicate only through pins and resolved nets. Integration into the complete RP2C02 package follows in subsequent milestones.


### VirtualHardware v0.40.0 — standalone RP2A03 package, phase 1

The Ricoh RP2A03 now exists as its own standalone chip package under `Components/Chips/Ricoh`.
This phase adds the physical package power, clock, CPU bus, interrupt, controller and audio pins;
the NTSC divide-by-12 M2 clock path; and the integrated 6502-derived execution section. The chip
has no motherboard, RAM, PPU, cartridge or renderer references. APU and controller-register internals
remain subsequent work on this same individual chip.


### VirtualHardware v0.42.0 — standalone RP2A03 controller I/O and OAM DMA

The standalone Ricoh RP2A03 package now implements its own internal `$4016` controller output register,
controller input reads through `IN0`/`IN1` with the corresponding `/OE1` and `/OE2` package strobes,
and the `$4014` OAM-DMA controller. DMA electrically owns the external CPU address/data bus for the
alignment cycle and 256 alternating source reads and `$2004` writes, then returns execution to the CPU
section. No motherboard, PPU, RAM, cartridge, controller device or renderer is referenced by the chip;
all transfer data enters and leaves through the RP2A03 package pins.


### VirtualHardware v0.42.0

The standalone RP2A03 now includes its four/five-step APU frame sequencer and both pulse-channel circuits (timer, duty sequencer, envelope, length counter, sweep, status and frame IRQ). These remain internal to the individual chip; no motherboard binding was added.

### VirtualHardware v0.43.0 — standalone RP2A03 triangle and noise channels

The standalone Ricoh `RP2A03` package now contains independent triangle and noise channel circuits. The triangle section implements the 32-step waveform sequencer, 11-bit timer, length counter, linear counter, control/reload behavior, and `$4008/$400A/$400B` register interface. The noise section implements the 15-bit linear-feedback shift register, long/short tap selection, NTSC period table, envelope, length counter, and `$400C/$400E/$400F` register interface. `$4015` now enables, disables, and reports all four completed non-DMC channels. Both circuits advance only from the chip's internal APU clocks and contribute to the package audio output without motherboard or device references.

### VirtualHardware v0.44.0 — standalone RP2A03 DMC channel

The standalone Ricoh RP2A03 package now contains its delta modulation channel: `$4010`–`$4013` control registers, NTSC rate divider, 7-bit output counter, sample address and length counters, sample buffer, output shift register, loop control, DMC IRQ state, `$4015` status/enable behavior, address wrap from `$FFFF` to `$8000`, and external sample reads performed through the package CPU address/data/control pins. DMC memory requests temporarily retain and restore the interrupted CPU read cycle; no RAM, cartridge, motherboard, or sample provider is referenced by the chip.

## v0.46.0 Standalone RP2A03 DMC/OAM DMA arbitration

- allows the internal DMC sample reader to take an eligible OAM DMA read slot on the shared external CPU bus;
- repeats the interrupted OAM source read before its paired `$2004` write so all 256 bytes remain ordered and uncorrupted;
- keeps DMC fetches out of OAM write slots and exposes an interleave counter for chip-level timing diagnostics;
- adds a standalone pin/bus-level regression test covering simultaneous DMC playback and OAM DMA.

## v0.45.0 Standalone RP2A03 APU accuracy hardening

The standalone RP2A03 now applies the documented nonlinear pulse and TND mixer transfer curves instead of adding channel DAC codes linearly. `$4017` writes use a phase-dependent three-or-four CPU-cycle delayed frame-counter reload, with immediate frame-IRQ clearing when inhibit is written. Dedicated chip tests verify nonlinear DAC output, internal `$4015` channel status, and delayed five-step mode activation. No motherboard or NES runtime wiring is introduced.



## v0.46.2 DMC/OAM DMA overlap regression correction

The standalone RP2A03 arbitration regression now executes two consecutive OAM DMA transfers while the DMC is active. This creates a deterministic external-bus ownership window long enough to include the DMC's retained power-up divider phase, without reaching into private chip state or assuming that a `$4010` rate write restarts the timer. The test still verifies ordered, uncorrupted OAM writes across both transfers.


## v0.47.0 Standalone RP2A03 DMC CPU stall sequencing

The standalone RP2A03 DMC DMA unit now retains the interrupted CPU read while it performs explicit halt, dummy, optional alignment, and external sample-read phases. A normal DMC fetch therefore stalls the integrated CPU section for three or four CPU cycles according to the current get/put phase. OAM DMA interleaving remains separate: because OAM already owns the CPU, the DMC takes an eligible OAM read slot and the repeated source read restores alignment without directly advancing CPU execution. Chip-level counters expose total DMC CPU-stall cycles and the most recent fetch length.



## v0.49.0 Standalone RP2A03 interrupt status-stack sequencing

The RP2A03 hardware-interrupt sequence now writes the pre-interrupt processor status to the external stack bus before asserting the internal interrupt-disable latch. IRQ and NMI therefore stack the original I flag with the break bit clear and the unused bit set, then set I before vector fetch. A chip-level regression drives /IRQ through a named package pin and verifies both the externally stored status byte and the post-entry internal flag state.

## v0.48.0 Standalone RP2A03 reset-state accuracy

The standalone RP2A03 now treats an asserted `/RES` pin as a hardware reset rather than a second power-on. The reset microsequence keeps the chip's existing stack pointer and decrements it through the three external stack-page read cycles, preserves existing arithmetic status state, forces interrupt disable, clears the non-physical break bit, and fetches the reset vector only through package pins. A chip-level regression verifies reset after executed code with a non-default stack pointer and carry state. No motherboard or NES runtime coupling is introduced.


## v0.50.0 Standalone RP2C02 package foundation

Version 0.50.0 begins the final standalone RP2C02 chip. The new reusable package exposes the RP2C02 power, reset, master-clock, CPU register, multiplexed VRAM address/data, high-address, control, extension and open-drain /NMI pins. Its first internal sections implement NTSC 341-by-262 raster counters, vblank state, the CPU-visible PPUCTRL and PPUSTATUS foundation, and pin-only package operation without motherboard, CPU, cartridge, renderer or memory references. Independent chip-level tests verify clock-driven raster progression and CPU register writes through the package pins.

### v0.52.1 — standalone RP2C02 CPU registers and VRAM bus

The standalone RP2C02 now implements its complete CPU-visible register block,
internal primary OAM access, scrolling/address latches, buffered PPUDATA reads,
and multiplexed external VRAM read/write transactions through the package pins.
No motherboard, cartridge, CPU, renderer, or external memory object is called.


## v0.52.1 — Standalone RP2C02 background pipeline

The standalone RP2C02 now performs background nametable, attribute and pattern-table reads exclusively through its multiplexed external VRAM pins while rendering is enabled. It owns the tile latches, pattern and attribute shift registers, coarse/fine scrolling progression and current background pixel index. No framebuffer, cartridge, motherboard or renderer dependency is introduced.


### v0.52.1
- Corrected the standalone RP2C02 background-pipeline test fixture so the external VRAM model supplies attribute bytes across the complete `$23C0-$23FF` attribute-table range.


## v0.53.0 — Standalone RP2C02 sprite pipeline

The standalone RP2C02 now performs scanline sprite evaluation from its internal primary OAM, copies up to eight in-range entries into secondary OAM, raises sprite overflow for additional entries, and fetches sprite pattern planes exclusively through the package VRAM pins. The chip supports 8×8 and 8×16 sprite addressing, vertical and horizontal flip attributes, X counters, sprite/background priority composition, sprite palette selection, and sprite-zero-hit state. No framebuffer, motherboard, cartridge, CPU, or renderer dependency was added.


## v0.54.1 — Standalone RP2C02 palette and output completion

CPU-visible reads are latched for the complete asserted `/CS` cycle, preventing repeated simulator settling from applying `PPUSTATUS` or `PPUDATA` side effects more than once.

- Added internal 32-byte palette RAM with universal-background mirroring.
- Added immediate palette PPUDATA reads and external mirrored-buffer refill.
- Added palette-masked PPUDATA writes, grayscale output and emphasis state.
- Added final output color-code generation and NMI falling-edge accounting.


## v0.55.0 — Standalone RP2C02 frame and NMI timing completion

The standalone RP2C02 now applies the NTSC odd-frame cycle skip when background or sprite rendering is enabled: odd pre-render scanlines advance directly from dot 339 to the next frame. Chip-level regressions also verify that enabling NMI during an already active vblank produces exactly one open-drain `/NMI` falling edge and keeps the line asserted without retriggering. This closes the remaining package-level raster and NMI timing foundation before CIC/3193 development begins.

## v0.56.0 — Standalone CIC/3193 package and serial foundation

Version 0.56.0 starts the final remaining standalone chip. `Cic3193` models the 16-pin 3193-series console lock package with named data, seed, configuration, clock, reset, host-reset, slave-reset, power and unused package pins. Power and external reset control all output ownership. A four-clock startup sequence independently releases the slave and host reset outputs, while the serial interface shifts input and output nibbles only on external clock rising edges. NTSC-only configuration and seed levels are sampled from their physical pins. The component contains no motherboard, cartridge, CPU or emulator callbacks; the exact authentication and retry state machine will be layered into this package next.


## v0.57.0 — Standalone CIC/3193 authentication and reset watchdog

Version 0.57.0 completes the CIC/3193 package-level lock behavior with a clocked four-round challenge/response state machine, continuous authenticated exchanges, invalid-response detection, an active-low host-reset retry hold, and automatic authentication restart. Every challenge and response bit still crosses only `DATA_OUT`, `DATA_IN`, and `CLK`; reset ownership remains entirely on the package pins. Diagnostic counters expose successful exchanges, failed exchanges, and generated host-reset pulses for independent chip validation without introducing motherboard or cartridge callbacks.


## v0.59.0 — RP2C02 PPUSTATUS/vblank race conformance

The standalone RP2C02 now models the scanline-241 `$2002` race at the package bus. A PPUSTATUS read already selected across the vblank-start boundary suppresses the vblank latch transition and prevents the corresponding open-drain `/NMI` falling edge. The behavior is driven only by the chip's clock and CPU bus pins, and an independent regression verifies the complete boundary cycle.


## v0.59.0 — RP2C02 completion sweep

This release consolidates the remaining high-value standalone RP2C02 conformance work before motherboard integration. It adds rendering-time `$2007` address-generator behavior, palette/open-bus read semantics, forced-blank palette output, rendering-time OAM port ownership, diagonal sprite-overflow evaluation, and expanded NMI edge validation.

The RP2C02 remains a package-level component driven only by power, reset, clock, CPU bus pins, VRAM bus pins, and internal state. It has no direct dependency on the RP2A03, cartridge, renderer, framebuffer, or motherboard.

Known residual silicon-level limitations are intentionally explicit: analog composite waveform generation, transistor-level open-bus decay, temperature/process-dependent power-up randomness, and decap-exact sub-dot metastability are outside the current digital virtual-hardware scope.
