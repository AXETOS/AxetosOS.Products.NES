# NES samples

The retired high-level `AxetosOS.Products.NES.HeadlessHost` execution path was removed in v1.3.7.
NES execution now uses the physical `VirtualHardware` machine only.

Run an NROM sample/ROM through the same physical IC-boundary host used for games:

```powershell
dotnet run -c Release --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost -- .\samples\axetos-cpu-smoke.nes --board famicom
```

Controller/input diagnostic samples will be reconnected through the physical controller package when the native controller adapter is completed; they are not routed through the removed reference emulator.


## v1.3.8 sampled physical-hardware profiler

The optional `--profile` desktop-host mode now measures the validated physical-IC runtime without changing the hardware architecture being exercised. Normal execution remains the same physical IC-boundary direct propagation path.

Profiler coverage includes:

- every motherboard-visible physical package, with activation count and sampled estimated CPU time;
- motherboard electrical trace resolution/presentation time and delivery counts;
- sampled RP2A03/RP2A07 internal CPU, DMA, APU and controller-I/O work;
- sampled RP2C02/RP2C07 internal CPU-port, raster, VRAM, background, sprite, video-output and package-output work;
- exact host-side time spent advancing the virtual hardware, presenting completed frames, transferring PCM audio, pumping native events and updating diagnostics/title text.

Component and electrical timings sample one in 256 hot operations. The profiling path keeps the compiled master-clock transport instead of falling back to the generic resolver, so diagnostics measure the same physical propagation architecture used by normal Release execution. Physical chips retain no profiler/simulator reference between reactions.

Example:

```powershell
dotnet run -c Release --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost -- "C:\ROMs\game.nes" --board famicom --profile
```


## v1.3.9 electrical transport hot path

Profiling v1.3.8 showed that the remaining runtime cost is dominated by physical electrical transport and the PPU external-memory path rather than the native video/audio presenter. v1.3.9 keeps the motherboard electrically dumb while reducing software work per delivered signal:

- ordinary `AnyChange` package pins no longer execute edge-counter/divider logic; edge bookkeeping remains only on edge-activated pins such as clocks;
- two-driver traces use a topology-compiled resolver instead of the generic driver-array scan;
- one/two-receiver traces avoid generic receiver loops;
- normal non-profile execution has a dedicated zero-profiler electrical path, while `--profile` retains the sampled diagnostics path;
- HM6116 and SN74LS373 retain power/output state so ordinary active bus traffic does not repeatedly rescan power pins or re-drive an already-present SRAM value.

All physical High/Low/Hi-Z changes are still delivered through motherboard traces. No signal is suppressed based on receiving-chip semantics; chip activation remains owned by the chip.

## v1.4.0 chip-owned pin activation gates

The v1.3.8 profiler showed that normal native presentation is inexpensive while the physical PPU/CIRAM/latch path produces tens of millions of package activations. v1.4.0 moves the cheap rejection point to the physical package pin without giving the motherboard any chip semantics.

Every motherboard level is still resolved and delivered. Each input pin always records the presented electrical state. A physical chip may then mark ordinary address/data pins as unable to wake its internal circuitry while its own power/select/enable logic disconnects that stage. Activation pins such as `/CS`, `/OE`, `/WE`, `LE`, clocks and other control inputs continue to wake the package normally so the chip itself can switch the relevant internal stage back on.

Key hot paths include RP2C02/RP2C07 CPU-port and external VRAM data inputs, RP2A03/RP2A07 synchronous CPU/controller inputs, HM6116 address/data stages, SN74LS373 latch data, SN74LS139/SN74LS368 disabled sections and NROM CPU/PPU data paths. The electrical layer also recognizes topology-proven single-driver traces directly without changing their electrical result.

The governing boundary remains: **motherboard transports electricity; chips own activation and all chip semantics**.

## v1.4.1 compiled shared-bus electrical transport

The v1.4.0 profile confirmed that chip-owned wake gates reduce package evaluations while the motherboard still performs billions of physical pin deliveries. v1.4.1 therefore optimizes the topology-only electrical work itself without allowing the motherboard to interpret chip semantics.

- traces with three or more possible drivers keep a compiled strong/weak electrical aggregate, updated only when an actual package driver publishes a new level or strength; CPU D and PPU AD shared buses no longer rescan every possible driver on every transition;
- one package reaction updates all of its changed driver states before an affected shared trace is resolved, preserving package-boundary atomicity when multiple package outputs share one physical trace;
- a trace affected by several outputs from the same package reaction is resolved once from the complete final driver state instead of performing a guaranteed-inert duplicate resolution;
- chip-gated `AnyChange` pins still store every delivered physical level and input history, but skip an unnecessary old-level comparison when their chip-owned wake gate is closed;
- output-only package-pin delivery uses a topology-proven store-only path, avoiding a repeated direction test;
- common 6-, 8-, 11- and 16-bit package bus sampling plus 8-bit strong drive/release paths are specialized for the NES hot widths.

No physical level transition is suppressed. Driver strength, contention, high-impedance behavior, bidirectional input history, receiver delivery, and chip-owned activation remain part of the same direct physical model.

## v1.4.2 direct shared-bus resolver + steady-state chip clock path

Measured Release benchmarks showed that v1.4.1's incremental multi-driver bookkeeping reduced the number of electrical operations but increased cost per hot shared-bus transition enough to regress Mario and Donkey Kong. v1.4.2 removes that duplicated driver-state bookkeeping while preserving the useful v1.4.1 pin-gating and fixed-width bus work.

- three- and four-driver traces use compact unrolled electrical resolution directly from the current physical package-pin drive states; larger traces fall back to the same direct scan model;
- package output batches remain atomic without keeping a second software copy of driver state: every changed pin already contains its final drive state before any affected trace is presented;
- the NTSC/PAL RP2C02/RP2C07 and RP2A03/RP2A07 packages recognize their dominant exact clock-only activation mask internally and bypass unrelated power/select/asynchronous mask decoding on steady-state clock work;
- the motherboard still resolves and delivers every physical level. Clock division, wake gating, reset/select meaning, bus sampling and all other activation semantics remain owned by the receiving physical chip/pin;
- four-driver regression coverage now exercises the real NROM-era CPU/PPU shared-bus driver count, including weak contention, strong override and unknown-drive behavior.

This is a profiler-guided rollback of the expensive v1.4.1 aggregate strategy, not a rollback of the dumb-motherboard / smart-chip architecture.
