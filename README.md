# AxetosOS Products / NES

> A modular, cycle-driven NES hardware emulator implemented as a native AxetosOS product.

[![Status](https://img.shields.io/badge/status-hardware%20foundation-orange)](#project-status)
[![Platform](https://img.shields.io/badge/platform-AxetosOS-informational)](#axetosos-native-product)
[![Language](https://img.shields.io/badge/language-C%23-512BD4)](#technology)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)

## Project status

This repository now contains the initial architecture, development roadmap, and first executable product foundation.

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
- [ ] Render the first PPU frame
- [ ] Produce the first correct APU audio sample
- [ ] Run the first playable game
- [ ] Publish the first working release

## Current foundation

The v0.3.0 CPU foundation establishes:

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
- an expanded RP2A03 CPU core with status flags, stack operations, subroutines, relative branches, arithmetic, BRK/RTI, IRQ and NMI entry;
- initial load/store/transfer instructions for A, X and Y;
- a 3:1 PPU-to-CPU master-clock scheduler;
- hardware tests covering RAM mirroring, NROM mapping, reset, instruction execution, clock ratios, stack/subroutine behavior, arithmetic flags, branches and NMI/RTI;
- an original legal NROM smoke-test ROM under `samples/`.

The headless host can inspect ROM metadata or boot an NROM cartridge for a selected number of CPU cycles. The CPU instruction set and cycle model remain intentionally incomplete at this stage; v0.3.0 establishes the state, stack and interrupt foundations needed for full instruction coverage.

```powershell
dotnet run --project .\src\Products\NES\AxetosOS.Products.NES.HeadlessHost -- "C:\ROMs\game.nes"

# Boot mapper 0 and execute a bounded number of CPU cycles
dotnet run --project .\src\Products\NES\AxetosOS.Products.NES.HeadlessHost -- "C:\ROMs\game.nes" --cycles 1000


# Run the repository-owned legal CPU smoke ROM
dotnet run --project .\src\Products\NES\AxetosOS.Products.NES.HeadlessHost -- .\samples\axetos-cpu-smoke.nes --cycles 1000
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

Expected development command:

```powershell
dotnet run --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost
```

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

The exact structure will be adjusted to match the latest AxetosOS source and product contracts before implementation begins.

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
- .NET 8 or the version currently used by AxetosOS when implementation begins;
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
- [ ] Inspect the latest AxetosOS source as the sole source of truth
- [ ] Confirm existing AxetosOS product contracts
- [ ] Confirm existing module lifecycle contracts
- [ ] Confirm project loading and Run Project integration
- [ ] Select the desktop-host technology
- [ ] Finalize public/private package boundaries

### Phase 1 — Product foundation

- [ ] Create the solution and product projects
- [ ] Add AxetosOS Core/Product SDK references
- [ ] Create the NES product manifest
- [ ] Register the NES product with AxetosOS
- [ ] Add Desktop Host
- [ ] Add Web Host
- [ ] Add Headless Host
- [ ] Add embedded Workbench host integration
- [ ] Add shared host service contracts
- [ ] Add product lifecycle tests
- [ ] Add public CI build

### Phase 2 — Hardware composition foundation

- [ ] Define base hardware module contract
- [ ] Define power-on lifecycle
- [ ] Define reset lifecycle
- [ ] Define deterministic state capture and restore
- [ ] Define clocked-module contract
- [ ] Define signal-line abstraction
- [ ] Define CPU bus
- [ ] Define PPU bus
- [ ] Define bus ownership and tri-state behaviour
- [ ] Define interrupt lines
- [ ] Define DMA request and arbitration model
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

- [ ] Parse iNES headers
- [ ] Parse NES 2.0 headers
- [ ] Validate ROM size and sections
- [ ] Extract PRG-ROM
- [ ] Extract CHR-ROM
- [ ] Support CHR-RAM declarations
- [ ] Read mapper number
- [ ] Read submapper number
- [ ] Read mirroring metadata
- [ ] Read battery-backed memory metadata
- [ ] Read trainer metadata
- [ ] Read region metadata
- [ ] Add ROM hash calculation
- [ ] Add known-header correction database support
- [ ] Add cartridge load diagnostics

### Phase 5 — Mapper and board definition engine

- [ ] Define mapper-catalog schema
- [ ] Define board-definition schema
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

- [ ] Define NROM-128 board
- [ ] Define NROM-256 board
- [ ] Attach PRG-ROM to CPU bus
- [ ] Attach CHR-ROM or CHR-RAM to PPU bus
- [ ] Implement horizontal mirroring wiring
- [ ] Implement vertical mirroring wiring
- [ ] Support optional PRG-RAM where applicable
- [ ] Validate NROM board assembly
- [ ] Run first NROM test cartridge

### Phase 7 — RP2A03 CPU core

- [ ] Define CPU pins and bus interface
- [ ] Implement CPU registers
- [ ] Implement status flags
- [ ] Implement reset sequence
- [ ] Implement stack behaviour
- [ ] Implement addressing modes
- [ ] Implement official opcodes
- [ ] Implement instruction micro-operations
- [ ] Implement cycle-accurate bus reads and writes
- [ ] Implement page-crossing timing
- [ ] Implement branch timing
- [ ] Implement IRQ handling
- [ ] Implement NMI handling
- [ ] Implement BRK behaviour
- [ ] Implement RDY and CPU stalls
- [ ] Decide scope for unofficial opcodes
- [ ] Add CPU trace output
- [ ] Pass selected CPU test ROMs

### Phase 8 — RP2C02 PPU

- [ ] Implement PPU register interface
- [ ] Implement PPU bus
- [ ] Implement VRAM address registers
- [ ] Implement scroll registers and write latch
- [ ] Implement nametable RAM
- [ ] Implement palette RAM
- [ ] Implement pattern-table reads
- [ ] Implement background fetch pipeline
- [ ] Implement shift registers
- [ ] Implement pixel composition
- [ ] Implement sprite OAM
- [ ] Implement secondary OAM
- [ ] Implement sprite evaluation
- [ ] Implement sprite rendering
- [ ] Implement sprite-zero hit
- [ ] Implement sprite overflow behaviour
- [ ] Implement VBlank timing
- [ ] Implement NMI timing
- [ ] Implement odd-frame timing behaviour
- [ ] Produce first correct framebuffer
- [ ] Pass selected PPU test ROMs

### Phase 9 — DMA and controllers

- [ ] Implement OAM DMA request
- [ ] Implement OAM DMA CPU stalls
- [ ] Implement DMA bus transfer sequencing
- [ ] Implement controller port 1
- [ ] Implement controller port 2
- [ ] Implement controller strobe behaviour
- [ ] Implement serial controller shift registers
- [ ] Add keyboard input adapter
- [ ] Add gamepad input adapter
- [ ] Validate input timing

### Phase 10 — APU and sound

- [ ] Implement APU register interface
- [ ] Implement frame sequencer
- [ ] Implement quarter-frame clocks
- [ ] Implement half-frame clocks
- [ ] Implement frame IRQ
- [ ] Implement pulse channel 1 timer and duty sequencer
- [ ] Implement pulse channel 2 timer and duty sequencer
- [ ] Implement pulse envelopes
- [ ] Implement pulse length counters
- [ ] Implement pulse sweep units
- [ ] Implement triangle timer and sequencer
- [ ] Implement triangle linear counter
- [ ] Implement triangle length counter
- [ ] Implement noise timer
- [ ] Implement noise LFSR
- [ ] Implement noise envelope
- [ ] Implement noise length counter
- [ ] Implement DMC output unit
- [ ] Implement DMC sample reader
- [ ] Implement DMC DMA
- [ ] Implement DMC CPU stalls
- [ ] Implement DMC IRQ
- [ ] Implement nonlinear pulse mixing
- [ ] Implement nonlinear TND mixing
- [ ] Implement deterministic audio sampling
- [ ] Implement resampler
- [ ] Implement host audio ring buffer
- [ ] Add desktop audio output
- [ ] Add Web Audio output
- [ ] Add APU diagnostic views
- [ ] Pass selected APU test ROMs
- [ ] Verify stable synchronized audio during gameplay

### Phase 11 — Host rendering and execution

- [ ] Implement NES framebuffer abstraction
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

- [ ] CPU unit tests
- [ ] PPU unit tests
- [ ] APU unit tests
- [ ] bus and signal tests
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

The project is currently in its initial planning phase. Contribution guidance will be added after the first product structure and hardware contracts are established.

## License

The public source in this repository is licensed under the [MIT License](LICENSE), unless a specific file states otherwise.

AxetosOS Core is a separate dependency and is not licensed or distributed by this repository.
