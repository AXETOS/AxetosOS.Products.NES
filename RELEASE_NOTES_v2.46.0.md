# AxetosOS Products / NES v2.46.0

## Mapper 69 / Sunsoft FME-7 / 5A / 5B family

v2.46.0 adds Mapper 69 as replaceable Sunsoft cartridge hardware while preserving the existing physical cartridge boundary and generic whole-circuit compiler.

The mapper package now owns:

- the `$8000-$9FFF` command-select and `$A000-$BFFF` command-data register interface;
- eight independently switchable 1 KiB CHR-ROM windows;
- three independently switchable 8 KiB PRG-ROM windows at `$8000-$DFFF` plus the fixed final 8 KiB bank at `$E000-$FFFF`;
- the bank-selectable `$6000-$7FFF` window, including PRG-ROM mode, enabled banked work RAM and disabled/open-bus RAM state;
- vertical, horizontal and both single-screen CIRAM routes;
- the 16-bit CPU-cycle IRQ down-counter, independent counter/output enable bits, underflow assertion and control-write acknowledgement;
- open-collector IRQ package output in both raw physical and compiled execution paths.

Mapper 69 metadata does not distinguish every FME-7/5A/5B ASIC revision. AxetosOS therefore represents the externally compatible family circuitry at the cartridge boundary and deliberately does not use ROM filenames, hashes or motherboard knowledge to guess a fitted chip revision.

## Sunsoft 5B PSG circuitry

The cartridge now also contains a dedicated `Sunsoft5bPsg` internal hardware block rather than implementing expansion sound in the motherboard or host. It models:

- the `$C000-$DFFF` audio-register select port and `$E000-$FFFF` data port;
- the select-port high-nibble data-write lockout;
- the Sunsoft 5B internal divide-by-16 generator clock;
- three 12-bit tone periods and phase generators;
- the 5-bit noise period, divide-by-two noise prescaler and 17-bit AY/YM LFSR generator;
- the 32-step YM-style envelope, 16-bit envelope period and all continue/attack/alternate/hold shape behavior;
- tone/noise mixer gating per channel;
- fixed-volume versus envelope-volume selection;
- logarithmic 32-step DAC state and mixed output-level diagnostics;
- CPU/PSG clock, generator, tone, noise, envelope and output-edge counters.

The current Famicom cartridge connector/runtime has no generic analog expansion-audio net. The PSG is therefore physically owned and clocked inside the cartridge, but its DAC output is not yet mixed into native PCM. That transport is intentionally deferred until it can be added as generic package/analog-signal infrastructure rather than as a Mapper-69 shortcut.

## Conformance and diagnostics

The validated incoming baseline is **569 / 569 Release tests** from v2.45.1.

v2.46.0 adds 27 Mapper-69/5B test cases, so the expected suite after applying this patch is **596 tests**. Coverage includes PRG/CHR address-line masking, the ROM/RAM/open-bus `$6000` window, banked RAM, legacy RAM policy, four CIRAM routes, IRQ enable/underflow/acknowledge behavior, raw M2 clocking, dynamic IRQ output, 5B register lockout, tone/noise/envelope generation, compiled/raw execution parity, invalid board geometry and factory composition.

Desktop shutdown diagnostics now print dedicated `Sunsoft FME-7`, `Sunsoft IRQ` and `Sunsoft 5B` lines.

A synthetic Mapper-69 smoke ROM is included at:

`samples/axetos-sunsoft-fme7-irq-5b.nes`

Run it with:

```powershell
dotnet run -c Release --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost -- .\samples\axetos-sunsoft-fme7-irq-5b.nes --board famicom --uncapped --stop-frame 120
```
