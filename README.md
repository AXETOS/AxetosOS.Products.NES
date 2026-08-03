# AxetosOS Products / NES

[![Status](https://img.shields.io/badge/status-playable-brightgreen)](#project-status)
[![Platform](https://img.shields.io/badge/platform-AxetosOS-informational)](#axetosos-native-product)
[![Language](https://img.shields.io/badge/language-C%23-512BD4)](#technology)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)

> A modular, cycle-driven NES hardware emulator implemented as a native AxetosOS product.

## Project status

The project is currently at **v0.22.0**. Supported cartridge images run in an AxetosOS-owned native desktop window with keyboard input, PCM audio, automatic console timing selection and live performance diagnostics.

Current highlights:

- modular CPU, PPU, APU, bus, controller and cartridge hardware;
- all documented RP2A03 CPU opcodes and addressing modes;
- background and sprite rendering;
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
| Escape | Exit |

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
