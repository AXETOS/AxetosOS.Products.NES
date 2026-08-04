# AxetosOS Products / NES

[![Status](https://img.shields.io/badge/status-playable-brightgreen)](#project-status)
[![Version](https://img.shields.io/badge/version-v0.23.0-blue)](#project-status)
[![Platform](https://img.shields.io/badge/platform-AxetosOS-informational)](#axetosos-native-product)
[![Language](https://img.shields.io/badge/language-C%23-512BD4)](#technology)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)

> A modular, cycle-driven NES hardware emulator implemented as a native AxetosOS product.

- Cycle-accurate PPU background fetch pipeline with nametable, attribute, pattern-plane, shift-register, coarse/fine scroll, and prefetch behavior.

## Project status

The project is currently at **v0.23.0**. Supported cartridge images run in an AxetosOS-owned native desktop window with keyboard input, PCM audio, automatic console timing selection and live performance diagnostics.

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


### VirtualHardware NES CPU motherboard slice (v0.26.0)

Version 0.26.0 moves the independent hardware simulator beyond isolated CPU fixtures. A reusable `NesCpuMotherboard` now composes the pin-driven MOS 6502, NTSC CPU oscillator, power-on reset circuit, control-line pull-ups, 2 KiB static work RAM, 32 KiB PRG ROM, address decoders and read-control inverter entirely through motherboard-owned nets. The board reproduces the NES internal RAM mirrors at `$0000-$1FFF` by leaving RAM address pins A11 and A12 physically unconnected, and supports NROM-128 16 KiB PRG mirroring into both `$8000-$BFFF` and `$C000-$FFFF` banks. A passive `Mos6502BusAnalyzer` observes resolved address, data, R/W, SYNC and PHI2 pins without direct component references and records external bus cycles for diagnostics and future compatibility comparison.

### VirtualHardware MOS 6502 execution core (v0.25.0)

The independent pin-driven MOS 6502 now includes a reusable execution core built entirely on external bus cycles. It includes the X and Y registers, status-flag handling, arithmetic and logic, register transfers, branches, stack operations and subroutine flow. Version 0.25.0 expands this foundation with zero-page indexed, absolute indexed, indexed-indirect and indirect-indexed addressing; NMOS-compatible indirect JMP page wrapping; accumulator and memory shifts/rotates; INC/DEC read-modify-write sequences with observable dummy and final writes; BIT; RTI; TSX; and TXS. The decoder now recognizes all 151 official opcode values, while opcode 0x00 intentionally remains a temporary test-program stop marker until a dedicated BRK sequence is introduced. Memory access remains visible only through A0-A15, D0-D7, R/W, SYNC, PHI2 and the control pins; it does not call the legacy emulator CPU or directly access a memory object.

## v0.27.0 VirtualHardware controller I/O slice

The independent pin-driven motherboard now includes an NES controller I/O package connected directly to the CPU address, data and R/W nets. Writes to `$4016` control the shared strobe, while reads from `$4016` and `$4017` shift the two independent eight-button registers in NES order: A, B, Select, Start, Up, Down, Left and Right. External button sources affect the package only through pins, and every controller access remains visible to the passive CPU bus analyzer.


## v0.29.0 VirtualHardware PPU register interface

The independent pin-wired NES motherboard now includes the CPU-facing RP2C02 register interface at $2000-$3FFF, including eight-register mirroring, PPUCTRL/PPUMASK, PPUSTATUS vblank clearing, OAMADDR/OAMDATA, PPUSCROLL/PPUADDR write-latch behavior, buffered PPUDATA reads, palette immediate reads, and 1/32 VRAM address increments. The component observes only bus pins and an external vblank signal; rendering remains outside this milestone.


### VirtualHardware v0.29.0 — clocked PPU timing and NMI foundation

The independent pin-driven motherboard now includes an NTSC RP2C02 timing core. It advances at three PPU cycles per CPU cycle, tracks all 341 dots across 262 scanlines, raises vblank at scanline 241 dot 1, and clears it at pre-render scanline 261 dot 1. `PPUCTRL` bit 7 is exported as an electrical NMI-enable signal. During vblank the PPU pulls the shared open-drain `/NMI` line low; otherwise the motherboard resistor pulls it high. The existing external vblank source remains available as a diagnostic force input and no legacy PPU implementation is invoked.
