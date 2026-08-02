# AxetosOS Products / NES

## v0.18.0 active PPU scroll-address correction

The v0.16.0 compatibility milestone separates the PPU's active rendering address from its temporary scroll address. Mid-frame `$2005`/`$2006` writes can now prepare upcoming nametable data without making the currently rendered playfield jump to the wrong screen. The background renderer latches the active `v` address per scanline and follows horizontal nametable wrapping from that state. Super Mario Bros. is the primary real-ROM regression target.

## v0.15.0 PPU sprite visibility correction

The v0.15.0 compatibility milestone corrects OAM Y-coordinate handling so a sprite stored at Y=$FF remains below the visible frame instead of wrapping onto scanline 0. This removes phantom sprite fragments at the top edge in real games such as Donkey Kong and adds a regression test for the hardware rule.

> A modular, cycle-driven NES hardware emulator implemented as a native AxetosOS product.

[![Status](https://img.shields.io/badge/status-interactive%20input-yellow)](#project-status)
[![Platform](https://img.shields.io/badge/platform-AxetosOS-informational)](#axetosos-native-product)
[![Language](https://img.shields.io/badge/language-C%23-512BD4)](#technology)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)

## Project status

The repository is currently at **v0.16.0**. The CPU executes all 151 official 6502 opcodes, the RP2C02 timing model advances alongside it, and the headless host can render an original NROM test cartridge to a 256×240 framebuffer image.

- [x] Define the product vision
- [x] Define the AxetosOS dependency model
- [x] Define the multi-host model
- [x] Define the modular virtual-hardware architecture
- [x] Define the data-driven cartridge and mapper strategy
- [x] Define the initial audio architecture
- [x] Create the AxetosOS product project structure
- [x] Implement the first executable host scaffold
- [x] Implement ROM header loading and inspection
- [x] Execute the first CPU instructions
- [x] Render the first PPU frame
- [x] Render the first visible tile background
- [x] Export a framebuffer image from the headless host
- [x] Produce the first APU audio sample
- [x] Load mapper and board definitions from JSON
- [x] Assemble NROM and UxROM cartridge hardware automatically
- [x] Execute an original mapper-2 bank-switching ROM
- [ ] Run the first playable game
- [ ] Publish the first working release

## Current foundation

The v0.14.0 native desktop audio milestone establishes:

- AxetosOS-owned Windows PCM playback through low-level native APIs;
- live connection from the emulated RP2A03 APU to the desktop speakers;
- no external audio framework or game engine;

- .NET 8 solution and product project structure;
- generic NES hardware-module and bus contracts;
- iNES and NES 2.0 header parsing;
- mapper/submapper catalog resolution;
- initial declarative NROM board definition;
- AxetosOS product manifest;
- headless ROM inspection host;
- initial unit tests for ROM parsing;
- a CPU bus with open-bus state;
- mirrored 2 KiB CPU work RAM;
- NROM-128 and NROM-256 PRG-ROM mapping;
- RP2A03 CPU reset-vector loading;
- all 151 official 6502 opcodes and documented addressing modes for the RP2A03 CPU core;
- indexed page-cross cycle penalties, zero-page pointer wrapping and the 6502 indirect-JMP page-wrap quirk;
- status flags, stack operations, subroutines, all conditional branches, arithmetic, logic, shifts, BRK/RTI, IRQ and NMI entry;
- initial load/store/transfer instructions for A, X and Y;
- a 3:1 PPU-to-CPU master-clock scheduler;
- hardware tests covering RAM mirroring, NROM mapping, reset, instruction execution, clock ratios, addressing modes, stack/subroutine behavior, arithmetic and logic flags, shifts, branches, indirect jumps and NMI/RTI;
- an original legal NROM smoke-test ROM under `samples/`;
- a 14-bit PPU bus with open-bus state;
- NROM CHR-ROM and CHR-RAM devices;
- 2 KiB CIRAM with horizontal and vertical nametable mirroring;
- palette RAM with NES universal-background aliases;
- RP2C02 CPU register mirroring, VRAM address/write latch and buffered PPUDATA reads;
- scanline, dot, frame, VBlank and NMI timing;
- a 256×240 framebuffer with visible background tiles, nametable lookup, attribute decoding, palette selection and basic scroll offsets;
- primary OAM storage, 8×8 and 8×16 sprite pattern addressing, sprite flipping, priority, sprite-zero hit and overflow groundwork;
- `$4014` OAM DMA transfers from CPU memory with 513/514-cycle CPU stalls;
- controller ports at `$4016/$4017` with strobe, latch and serial shift-register behavior;
- independent controller 1 and controller 2 state supplied through a host-neutral input abstraction;
- inspectable PPU current/temporary VRAM address, fine-X scroll and first/second-write latch state;
- rendering-time coarse-X and vertical VRAM increments with horizontal and vertical nametable wrapping;
- deterministic cycle-based controller input scripts for reproducible headless runs;
- an original legal controller-motion NROM and input timeline under `samples/`;
- rendering-time horizontal and pre-render vertical VRAM-address transfer groundwork;
- PPM framebuffer export from the headless host;
- an original legal PPU background test ROM under `samples/`;
- an RP2A03 APU register device with pulse, triangle, noise and DMC channel foundations;
- quarter-frame and half-frame sequencing for envelopes, linear counters, length counters and pulse sweeps;
- nonlinear NES pulse/TND mixing including DMC, deterministic 44.1 kHz sample generation and NES-style high-pass/low-pass filtering;
- WAV export from the headless host;
- original legal APU tone and DMC sample ROMs under `samples/`.

The native desktop host can now run supported NROM and UxROM cartridges in an AxetosOS-owned Win32 framebuffer window with live keyboard input. The headless host can inspect ROM metadata or boot supported NROM and UxROM cartridges for a selected number of CPU cycles while the RP2C02 advances at the NTSC 3:1 PPU-to-CPU clock ratio. v0.12.0 extends the RP2A03 APU with frame-counter IRQs, DMC sample reads through the CPU bus, four-cycle DMC CPU stalls, looping and IRQ behavior, nonlinear DMC mixing, deterministic PCM generation, NES-style output filtering and WAV export. Secondary OAM, exact sprite evaluation, the full background fetch/shift-register pipeline, and cycle-level DMA sequencing remain future milestones.

```powershell

# Run the repository-owned UxROM bank-switching test
dotnet run --project .\src\Products\NES\AxetosOS.Products.NES.HeadlessHost -- .\samples\axetos-uxrom-bank-switch.nes --cycles 1000
# Generate audio from the repository-owned APU tone ROM
dotnet run --project .\src\Products\NES\AxetosOS.Products.NES.HeadlessHost -- .\samples\axetos-apu-tone.nes --cycles 180000 --audio .\output\apu-tone.wav

# Generate DMC sample audio (about five seconds)
dotnet run --project .\src\Products\NES\AxetosOS.Products.NES.HeadlessHost -- .\samples\axetos-apu-dmc.nes --cycles 9000000 --audio .\output\apu-dmc.wav

dotnet run --project .\src\Products\NES\AxetosOS.Products.NES.HeadlessHost -- "C:\ROMs\game.nes"

# Boot mapper 0 and execute a bounded number of CPU cycles
dotnet run --project .\src\Products\NES\AxetosOS.Products.NES.HeadlessHost -- "C:\ROMs\game.nes" --cycles 1000


# Run the repository-owned legal CPU smoke ROM
dotnet run --project .\src\Products\NES\AxetosOS.Products.NES.HeadlessHost -- .\samples\axetos-cpu-smoke.nes --cycles 1000

# Run with a deterministic controller state
dotnet run --project .\src\Products\NES\AxetosOS.Products.NES.HeadlessHost -- .\samples\axetos-ppu-sprites.nes --cycles 120000 --controller1 A,Start,Right --frame .\output\ppu-sprites-input.ppm

# Run the original controller-motion ROM with cycle-based scripted input
dotnet run --project .\src\Products\NES\AxetosOS.Products.NES.HeadlessHost -- .\samples\axetos-controller-motion.nes --cycles 420000 --input-script .\samples\axetos-controller-motion.input.json --frame .\output\controller-motion.ppm

# Render the repository-owned sprite and DMA test ROM
dotnet run --project .\src\Products\NES\AxetosOS.Products.NES.HeadlessHost -- .\samples\axetos-ppu-sprites.nes --cycles 120000 --frame .\output\ppu-sprites.ppm

# Render the repository-owned legal PPU background ROM to an image
dotnet run --project .\src\Products\NES\AxetosOS.Products.NES.HeadlessHost -- .\samples\axetos-ppu-background.nes --cycles 100000 --frame .\output\ppu-background.ppm
```

Commercial ROM files are not included and must never be committed.

## Vision

AxetosOS Products / NES is intended to be more than a conventional software emulator.

The project will reconstruct the NES as a collection of independently emulated hardware components connected through explicit buses, clocks, signal lines, memory devices and cartridge circuitry.

The emulator will run because its virtual hardware behaves correctly together—not because the product contains game-specific shortcuts or one monolithic emulator class.

The project is also intended to demonstrate the flexibility of AxetosOS Core. The same platform that hosts business products such as CRM can also compose and run a deterministic, real-time hardware-emulation product with custom rendering, audio, input and diagnostics.

## AxetosOS-native product

This project is intentionally **not a standalone NES emulator**.

It is a native AxetosOS product and requires AxetosOS Core for:

- product discovery and loading;
- module composition;
- dependency resolution;
- lifecycle management;
- host selection;
- runtime scheduling;
- diagnostics and observability;
- rendering, audio and input service integration;
- project execution through AxetosOS Workbench;
- state and resource management.

The public NES repository demonstrates how a complex product consumes AxetosOS platform capabilities without publishing the proprietary AxetosOS Core implementation.

The expected AxetosOS workflow is:

```text
Open AxetosOS
    -> Load Products/NES
    -> Resolve product modules and host capabilities
    -> Run Project
    -> Select or use the configured host
    -> Assemble and start the virtual NES
```

## Product location

Within an AxetosOS source tree, the product is expected to reside under:

```text
AxetosOS/
└── Products/
    └── NES/
```

The public repository name is:

```text
AxetosOS.Products.NES
```

## Core architectural principle

Every significant NES hardware component will be represented as an independently testable and inspectable module.

```text
AxetosOS NES Console
├── Master clock
├── RP2A03 processor package
│   ├── 6502-derived CPU core
│   ├── APU
│   ├── DMA logic
│   └── controller I/O
├── RP2C02 PPU
├── CPU work RAM
├── CIRAM / nametable RAM
├── palette RAM
├── controller ports
├── CPU bus
├── PPU bus
├── interrupt and control signal lines
└── cartridge connector
    └── virtual cartridge PCB
```

The physical NES packages and their internal functional units will be modelled at a practical hierarchy:

```text
AxetosOS module
    -> physical chip package
        -> internal hardware units
            -> registers, counters, latches and sequencers
```

The project initially targets **cycle-driven, component-level and signal-oriented emulation**. It does not initially target transistor-level simulation.

## Hardware modules

### RP2A03 processor package

The RP2A03 composition is planned to contain:

- 6502-derived CPU core;
- CPU registers and status flags;
- instruction decoder;
- cycle and micro-operation sequencer;
- address and data bus interface;
- RESET, IRQ, NMI and RDY handling;
- OAM DMA controller;
- DMC DMA controller;
- APU register interface;
- controller I/O.

### RP2C02 PPU

The PPU module is planned to contain:

- CPU-facing PPU registers;
- PPU address and data bus interface;
- VRAM address and scrolling registers;
- background fetch pipeline;
- pattern table access;
- nametable access;
- attribute-table decoding;
- palette RAM access;
- primary and secondary OAM;
- sprite evaluation;
- sprite-zero hit behaviour;
- VBlank and NMI behaviour;
- dot, scanline and frame timing;
- framebuffer output.

### APU

Audio is a first-class subsystem and will not be postponed until the end of development.

```text
APU
├── Pulse channel 1
├── Pulse channel 2
├── Triangle channel
├── Noise channel
├── DMC channel
├── Frame sequencer
├── Length counters
├── Envelope generators
├── Sweep units
├── Nonlinear mixer
└── audio resampling/output pipeline
```

The APU emulation will remain separate from host audio playback:

```text
NES hardware clock
    -> APU channels
    -> NES nonlinear mixer
    -> deterministic emulated audio signal
    -> resampler
    -> host audio buffer
    -> desktop or browser audio output
```

The desktop or browser host must never dictate APU hardware behaviour.

## Bus and signal model

Components will communicate through narrow hardware-oriented contracts rather than references to a global console object.

Planned concepts include:

- CPU address bus;
- CPU data bus;
- CPU read/write control;
- PPU address bus;
- PPU data bus;
- IRQ line;
- NMI line;
- RESET line;
- RDY line;
- DMA requests and bus ownership;
- cartridge chip-select signals;
- CIRAM address-enable control;
- clock edges and clock divisions.

The CPU should perform bus cycles instead of directly indexing a global memory array. Devices will respond only when selected by address decoding and control signals.

## Cartridge hardware and mapper strategy

A `.nes` ROM normally contains the game program and graphics data together with metadata identifying the expected cartridge hardware. It does not contain a software implementation of the mapper circuitry.

The loader will use iNES or NES 2.0 metadata to determine:

- mapper number;
- submapper;
- PRG-ROM size;
- CHR-ROM or CHR-RAM size;
- PRG-RAM and battery-backed memory requirements;
- mirroring mode;
- region and other cartridge details.

The mapper number will resolve to a **data-driven cartridge-board definition**.

```text
ROM header
    -> mapper/submapper catalog
    -> cartridge-board definition
    -> hardware component definitions
    -> AxetosOS hardware assembler
    -> running virtual cartridge
```

The architecture will avoid a conventional design based on:

```text
Mapper0.cs
Mapper1.cs
Mapper2.cs
Mapper3.cs
...
```

Instead, mapper and board support will be declared using reusable hardware primitives and JSON definitions.

### Example mapper catalog

```json
{
  "definitions": [
    {
      "mapper": 0,
      "submapper": null,
      "definition": "hardware/boards/nrom.json"
    },
    {
      "mapper": 1,
      "submapper": null,
      "definition": "hardware/boards/sxrom-mmc1.json"
    },
    {
      "mapper": 2,
      "submapper": null,
      "definition": "hardware/boards/uxrom.json"
    },
    {
      "mapper": 4,
      "submapper": 0,
      "definition": "hardware/boards/txrom-mmc3.json"
    }
  ]
}
```

### Example board definition concept

```json
{
  "mapper": 2,
  "name": "UxROM",
  "components": [
    {
      "id": "prg",
      "type": "Rom",
      "source": "PrgRom"
    },
    {
      "id": "chr",
      "type": "Ram",
      "size": 8192
    },
    {
      "id": "bankLatch",
      "type": "Latch",
      "width": 4
    },
    {
      "id": "writeDecoder",
      "type": "AddressDecoder",
      "range": "0x8000-0xFFFF",
      "operation": "Write"
    }
  ],
  "connections": [
    "Cpu.Data[0..3] -> bankLatch.Input",
    "writeDecoder.Output -> bankLatch.Enable",
    "bankLatch.Output -> prg.BankSelect",
    "Cpu.Address[0..13] -> prg.Address[0..13]"
  ]
}
```

Simple cartridge boards should be expressible entirely through reusable components and wiring data. Complex custom ASICs such as MMC1 or MMC3 may initially use dedicated chip modules with explicit pins and internal state. Those chip modules can later be replaced by lower-level declarative logic without changing the cartridge-board interface.

## Reusable virtual-electronics primitives

The project is expected to develop a reusable digital-hardware definition layer containing components such as:

- ROM and RAM devices;
- registers;
- transparent and edge-triggered latches;
- counters;
- shift registers;
- comparators;
- multiplexers;
- address decoders;
- edge detectors;
- bus transceivers;
- tri-state outputs;
- IRQ generators;
- logic gates;
- clock dividers;
- signal connections.

These primitives will allow many cartridge variants to be added as data rather than compiled mapper-specific source code.

## Multiple AxetosOS hosts

The product will support several AxetosOS hosts. All hosts will run the same NES hardware product and provide only environment-specific services.

### Desktop host

The normal local application host will open its own emulator window and provide:

- native or desktop rendering;
- low-latency audio output;
- keyboard and gamepad input;
- ROM selection;
- debugging and hardware-inspection panels.

Development command:

```powershell
dotnet run --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost -- "C:\\ROMs\\game.nes"
```

Controls: arrows for the D-pad, `Z` for A, `X` for B, Enter for Start, Right Shift for Select, and Escape to close. The host uses the private AxetosOS native framebuffer presenter; no external rendering framework is used.

### Web host

The web host will provide an optional browser execution environment using a configured URL.

Expected development command:

```powershell
dotnet run --project .\src\Products\NES\AxetosOS.Products.NES.WebHost --urls "http://127.0.0.1:5011"
```

The web host is expected to use browser technologies such as:

- Canvas or WebGL;
- Web Audio;
- keyboard input;
- Gamepad API.

### Headless host

The headless host will support:

- automated test ROM execution;
- CPU traces;
- frame hashes;
- audio hashes;
- compatibility checks;
- performance benchmarks;
- deterministic CI validation.

Expected development command:

```powershell
dotnet run --project .\src\Products\NES\AxetosOS.Products.NES.HeadlessHost -- --rom .\tests\roms\cpu-test.nes
```

### Embedded AxetosOS host

The product will also be loadable directly inside AxetosOS Workbench:

```text
Load Project
    -> Products/NES
    -> Run Project
```

The Workbench will be able to host the emulator surface and open hardware inspectors for individual modules.

## Host responsibilities

Hosts may provide:

- video output;
- audio output;
- input devices;
- file selection;
- window management;
- platform-specific timing services;
- diagnostic presentation.

Hosts must not implement:

- CPU instructions;
- PPU behaviour;
- APU behaviour;
- DMA behaviour;
- cartridge mapping;
- NES timing rules.

The rule is:

> Hosts adapt AxetosOS to an execution environment; they do not implement NES hardware behaviour.

## Diagnostics and virtual hardware laboratory

AxetosOS should make the emulated machine visible instead of hiding it behind the game screen.

Planned diagnostic tools include:

- virtual motherboard view;
- CPU register inspector;
- instruction trace and disassembler;
- stack viewer;
- CPU and PPU memory viewers;
- CPU bus activity monitor;
- PPU bus activity monitor;
- IRQ and NMI signal timeline;
- DMA ownership and transfer trace;
- PPU pattern-table viewer;
- nametable viewer;
- palette viewer;
- OAM and sprite viewer;
- APU channel waveform views;
- envelope, sweep and length-counter inspectors;
- DMC sample and DMA inspector;
- cartridge PCB inspector;
- mapper register and bank-selection inspector;
- master-clock, CPU-cycle, PPU-dot, scanline and frame stepping.

Selecting a virtual chip in AxetosOS should expose its live state and signals.

## Planned repository structure

The repository currently uses the following product-oriented structure. Additional host and AxetosOS integration projects will be added as their contracts are implemented.

```text
AxetosOS.Products.NES/
├── README.md
├── LICENSE
├── .gitignore
├── src/
│   └── Products/
│       └── NES/
│           ├── AxetosOS.Products.NES
│           ├── AxetosOS.Products.NES.Hardware
│           ├── AxetosOS.Products.NES.Cartridges
│           ├── AxetosOS.Products.NES.Audio
│           ├── AxetosOS.Products.NES.Diagnostics
│           ├── AxetosOS.Products.NES.DesktopHost
│           ├── AxetosOS.Products.NES.WebHost
│           └── AxetosOS.Products.NES.HeadlessHost
├── hardware/
│   ├── mapper-catalog.json
│   ├── boards/
│   └── chips/
├── tests/
└── docs/
```

## Technology

The expected technology direction is:

- C#;
- .NET 8;
- AxetosOS Core and product/module contracts;
- a desktop rendering and audio host selected during implementation;
- Blazor and browser APIs for the web host;
- JSON Schema for mapper, chip and board definitions;
- xUnit or the established AxetosOS test framework;
- GitHub Actions for public CI.

## Development roadmap

The roadmap is intentionally detailed so completed work can be checked off directly in this README.

### Phase 0 — Repository and architecture

- [x] Create initial repository
- [x] Document the product vision
- [x] Document the AxetosOS-native dependency model
- [x] Document the multi-host model
- [x] Document the modular hardware architecture
- [x] Document the mapper-definition strategy
- [x] Document the initial APU strategy
- [ ] Confirm existing AxetosOS product contracts
- [ ] Confirm existing module lifecycle contracts
- [ ] Confirm project loading and Run Project integration
- [x] Select the desktop-host technology
- [ ] Finalize public/private package boundaries

### Phase 1 — Product foundation

- [x] Create the solution and product projects
- [ ] Add AxetosOS Core/Product SDK references
- [x] Create the NES product manifest
- [ ] Register the NES product with AxetosOS
- [x] Add Desktop Host
- [ ] Add Web Host
- [x] Add Headless Host
- [ ] Add embedded Workbench host integration
- [ ] Add shared host service contracts
- [ ] Add product lifecycle tests
- [ ] Add public CI build

### Phase 2 — Hardware composition foundation

- [x] Define base hardware module contract
- [x] Define power-on lifecycle
- [x] Define reset lifecycle
- [ ] Define deterministic state capture and restore
- [x] Define clocked-module contract
- [x] Define signal-line abstraction
- [x] Define CPU bus
- [x] Define PPU bus
- [ ] Define bus ownership and tri-state behaviour
- [x] Define interrupt lines
- [x] Define DMA request and arbitration model
- [ ] Define NES motherboard/backplane composition
- [ ] Add module and connection validation
- [ ] Add deterministic hardware trace format

### Phase 3 — Memory and digital logic

- [ ] Implement ROM device
- [ ] Implement RAM device
- [ ] Implement mirrored memory device
- [ ] Implement register primitive
- [ ] Implement latch primitive
- [ ] Implement shift-register primitive
- [ ] Implement counter primitive
- [ ] Implement comparator primitive
- [ ] Implement multiplexer primitive
- [ ] Implement address decoder
- [ ] Implement edge detector
- [ ] Implement clock divider
- [ ] Implement reusable logic gates
- [ ] Add primitive-level tests

### Phase 4 — ROM and cartridge loading

- [x] Parse iNES headers
- [x] Parse NES 2.0 headers
- [x] Validate ROM size and sections
- [x] Extract PRG-ROM
- [x] Extract CHR-ROM
- [x] Support CHR-RAM declarations
- [x] Read mapper number
- [x] Read submapper number
- [x] Read mirroring metadata
- [x] Read battery-backed memory metadata
- [x] Read trainer metadata
- [ ] Read region metadata
- [ ] Add ROM hash calculation
- [ ] Add known-header correction database support
- [ ] Add cartridge load diagnostics

### Phase 5 — Mapper and board definition engine

- [x] Define mapper-catalog schema
- [x] Define board-definition schema
- [ ] Define chip-definition schema
- [ ] Define component connection syntax
- [ ] Define inheritance/extends support
- [ ] Define board override support
- [ ] Validate definitions before assembly
- [ ] Build hardware-definition loader
- [ ] Build cartridge hardware assembler
- [ ] Support external definition packs
- [ ] Add clear unsupported-mapper diagnostics
- [ ] Add definition versioning
- [ ] Add automated definition tests

### Phase 6 — NROM / Mapper 0

- [x] Define NROM-128 board
- [x] Define NROM-256 board
- [x] Attach PRG-ROM to CPU bus
- [x] Attach CHR-ROM or CHR-RAM to PPU bus
- [x] Implement horizontal mirroring wiring
- [x] Implement vertical mirroring wiring
- [ ] Support optional PRG-RAM where applicable
- [x] Validate NROM board assembly
- [x] Run first NROM test cartridge

### Phase 7 — RP2A03 CPU core

- [x] Define CPU bus interface
- [x] Implement CPU registers
- [x] Implement status flags
- [x] Implement reset sequence
- [x] Implement stack behaviour
- [x] Implement addressing modes
- [x] Implement all 151 official opcodes
- [ ] Implement instruction micro-operations
- [ ] Implement cycle-accurate bus reads and writes
- [x] Implement page-crossing timing
- [x] Implement branch timing
- [x] Implement IRQ handling
- [x] Implement NMI handling
- [x] Implement BRK behaviour
- [ ] Implement RDY and CPU stalls
- [ ] Decide scope for unofficial opcodes
- [ ] Add CPU trace output
- [ ] Pass selected CPU test ROMs

### Phase 8 — RP2C02 PPU

- [x] Implement PPU register interface
- [x] Implement PPU bus
- [x] Implement VRAM address registers
- [x] Implement scroll registers and write latch
- [x] Implement coarse-X increment and horizontal nametable wrapping
- [x] Implement fine/coarse-Y increment and vertical nametable wrapping
- [x] Implement horizontal address transfer at dot 257
- [x] Implement pre-render vertical address transfer
- [x] Implement nametable RAM
- [x] Implement palette RAM
- [x] Implement pattern-table reads
- [ ] Implement background fetch pipeline
- [ ] Implement shift registers
- [x] Implement background pixel composition
- [x] Implement sprite OAM
- [ ] Implement secondary OAM
- [x] Implement sprite evaluation
- [x] Implement sprite rendering
- [x] Implement sprite-zero hit
- [x] Implement sprite overflow behaviour
- [x] Implement VBlank timing
- [x] Implement NMI timing
- [ ] Implement odd-frame timing behaviour
- [x] Produce first visible background framebuffer
- [ ] Pass selected PPU test ROMs

### Phase 9 — DMA and controllers

- [x] Implement OAM DMA request
- [x] Implement OAM DMA CPU stalls
- [ ] Implement DMA bus transfer sequencing
- [x] Implement controller port 1
- [x] Implement controller port 2
- [x] Implement controller strobe behaviour
- [x] Implement serial controller shift registers
- [x] Add deterministic headless input scripting
- [x] Add controller-driven original test ROM
- [ ] Add keyboard input adapter
- [ ] Add gamepad input adapter
- [ ] Validate input timing

### Phase 10 — APU and sound

- [x] Implement APU register interface
- [x] Implement frame sequencer
- [x] Implement quarter-frame clocks
- [x] Implement half-frame clocks
- [x] Implement frame IRQ
- [x] Implement pulse channel 1 timer and duty sequencer
- [x] Implement pulse channel 2 timer and duty sequencer
- [x] Implement pulse envelopes
- [x] Implement pulse length counters
- [x] Implement pulse sweep units
- [x] Implement triangle timer and sequencer
- [x] Implement triangle linear counter
- [x] Implement triangle length counter
- [x] Implement noise timer
- [x] Implement noise LFSR
- [x] Implement noise envelope
- [x] Implement noise length counter
- [x] Implement DMC output unit
- [x] Implement DMC sample reader
- [x] Implement DMC DMA
- [x] Implement DMC CPU stalls
- [x] Implement DMC IRQ
- [x] Implement nonlinear pulse mixing
- [x] Implement nonlinear TND mixing
- [x] Implement deterministic audio sampling
- [x] Implement fixed-rate host sample conversion and NES output filtering
- [ ] Implement host audio ring buffer
- [x] Add desktop audio output
- [ ] Add Web Audio output
- [ ] Add APU diagnostic views
- [ ] Pass selected APU test ROMs
- [ ] Verify stable synchronized audio during gameplay

### Phase 11 — Host rendering and execution

- [x] Expose NES framebuffer output
- [ ] Implement nearest-neighbour scaling
- [ ] Implement desktop renderer
- [ ] Implement browser Canvas/WebGL renderer
- [ ] Implement frame pacing
- [ ] Implement audio-driven pacing feedback
- [ ] Avoid changing emulated hardware timing to repair host buffering
- [ ] Add fullscreen mode
- [ ] Add configurable integer scaling
- [ ] Add optional CRT-style presentation filters
- [ ] Add pause
- [ ] Add reset
- [ ] Add power cycle
- [ ] Add frame stepping
- [ ] Add CPU-cycle stepping
- [ ] Add PPU-dot stepping

### Phase 12 — Save data and state

- [ ] Support battery-backed cartridge RAM
- [ ] Define `.sav` file handling
- [ ] Define deterministic save-state format
- [ ] Capture CPU state
- [ ] Capture PPU state
- [ ] Capture APU state
- [ ] Capture RAM state
- [ ] Capture cartridge/mapper state
- [ ] Capture clock and bus state
- [ ] Restore state without timing drift
- [ ] Add save-state compatibility versioning
- [ ] Add host save/load controls

### Phase 13 — Diagnostics and debugger

- [ ] Add motherboard view
- [ ] Add live CPU register inspector
- [ ] Add disassembler
- [ ] Add execution breakpoints
- [ ] Add memory breakpoints
- [ ] Add bus-address breakpoints
- [ ] Add stack viewer
- [ ] Add CPU memory viewer
- [ ] Add PPU memory viewer
- [ ] Add pattern-table viewer
- [ ] Add nametable viewer
- [ ] Add palette viewer
- [ ] Add sprite/OAM viewer
- [ ] Add CPU bus monitor
- [ ] Add PPU bus monitor
- [ ] Add IRQ/NMI timeline
- [ ] Add DMA trace
- [ ] Add cartridge PCB inspector
- [ ] Add mapper bank-state inspector
- [ ] Add APU waveform viewer
- [ ] Add APU sequencer/counter inspector
- [ ] Add trace export

### Phase 14 — Additional cartridge hardware

- [ ] CNROM board definitions
- [ ] UxROM board definitions
- [ ] AxROM board definitions
- [ ] MMC1 chip and SxROM boards
- [ ] MMC3/MMC6 chip and TxROM boards
- [ ] Bus-conflict variants
- [ ] Four-screen nametable support
- [ ] Mapper IRQ support
- [ ] Board-specific RAM protection
- [ ] Additional audio hardware framework
- [ ] External mapper-definition pack loading

### Phase 15 — Product experience

- [ ] ROM open command
- [ ] Drag-and-drop ROM loading
- [ ] Recent ROM list
- [ ] ROM metadata inspector
- [ ] Supported/unsupported hardware report
- [ ] Input configuration
- [ ] Audio configuration
- [ ] Video configuration
- [ ] Region selection where supported
- [ ] Hardware diagnostic workspace presets
- [ ] Clear error handling
- [ ] Product help and documentation

### Phase 16 — Quality and compatibility

- [x] CPU unit tests
- [x] PPU unit tests
- [ ] APU unit tests
- [x] bus and signal tests
- [ ] mapper-definition tests
- [ ] deterministic replay tests
- [ ] save-state round-trip tests
- [ ] frame-hash tests
- [ ] audio-hash tests
- [ ] test-ROM suite execution
- [ ] performance benchmarks
- [ ] memory-allocation benchmarks
- [ ] long-running stability test
- [ ] compatibility matrix
- [ ] document known inaccuracies

### Phase 17 — Public release

- [ ] Confirm repository builds in public CI
- [ ] Add architecture diagrams
- [ ] Add screenshots
- [ ] Add demonstration video
- [ ] Add compatibility document
- [ ] Add contribution guidelines
- [ ] Add issue templates
- [ ] Add release notes
- [ ] Tag first preview release
- [ ] Tag first stable release

## Initial release target

The first finished public release should be scoped and honest rather than claiming universal NES compatibility.

A strong initial target is:

- native AxetosOS product;
- Desktop, Web, Headless and embedded execution paths;
- modular CPU, PPU, APU, RAM, buses and cartridge hardware;
- automatic mapper recognition from ROM metadata;
- data-driven cartridge-board definitions;
- working NROM support;
- playable video and controller input;
- complete core APU channels including DMC where practical;
- stable audio output;
- save states;
- hardware inspectors;
- automated tests and public CI;
- documented compatibility and limitations.

Additional mapper support can be released incrementally without redesigning the emulator core.

## Definition of done

A feature is not considered complete merely because one commercial game appears to work.

A hardware feature should normally include:

- implementation;
- deterministic tests;
- relevant test-ROM verification;
- diagnostic visibility;
- documented known limitations;
- state capture and restore support where applicable;
- compatibility with all supported hosts.

## Legal and copyright

This repository will not include commercial NES ROMs, copyrighted game assets or cartridge dumps.

Development and testing should use:

- original test ROMs;
- homebrew software with clear redistribution rights;
- permissively licensed emulator test suites where redistribution is allowed;
- legally obtained local cartridge dumps that are not committed to the repository.

ROM files, save files, traces and generated runtime data should remain excluded from source control unless they are specifically licensed project test assets.

Nintendo Entertainment System and NES are trademarks of Nintendo. This project is not affiliated with or endorsed by Nintendo.

## Repository policy

The repository should contain:

- source code;
- hardware definitions;
- schemas;
- tests;
- documentation;
- legal test assets where permitted;
- screenshots and demonstration media created for this project.

The repository should not contain:

- commercial ROMs;
- private AxetosOS Core source;
- passwords or secrets;
- local user settings;
- build output;
- generated traces;
- save files;
- copyrighted game assets.

## Contributions

The project is under active early development. Contribution guidance will be added after the first playable product milestone and the public AxetosOS product contracts have stabilized.

## License

The public source in this repository is licensed under the [MIT License](LICENSE), unless a specific file states otherwise.

AxetosOS Core is a separate dependency and is not licensed or distributed by this repository.
