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

## v1.4.3 chip-local direct reaction experiment

v1.4.3 tested additional branch-heavy chip-local shortcuts. The architecture remained correct, but measured normal Release performance regressed from the v1.4.2 baseline: Mario 22.55 -> 21.83 FPS and Donkey Kong 21.04 -> 20.86 FPS. Those losing SRAM/latch/PPU hot-path branches are not carried forward as the performance strategy. The useful RP2A03/RP2A07 controller-input enable gating remains chip-owned.

## v1.4.4 RP2C0x internal phase/decode hot path

v1.4.4 moves the performance focus inside the physical PPU package. PPUCTRL and PPUMASK are retained package registers whose decoded internal control lines now update only when those registers change, rather than re-decoding the same bits on every dot. Post-render/vblank dots bypass the rendering pipelines, non-visible fetch phases no longer recompute the pixel-color mux, and the eight sprite output units select the current sprite pixel and advance their counters/shifters in one pass instead of two.

The external architecture is unchanged: every physical clock and trace transition still reaches the package, the motherboard remains topology/electrical-only, and all rendering/fetch/scroll/NMI/VRAM behavior remains owned by RP2C02/RP2C07.

## v1.5.0 RP2C0x hardwired chip core

v1.5.0 begins the chip-core redesign with the physical RP2C02/RP2C07 package. The external package and motherboard boundary is unchanged, but the PPU no longer treats each dot as a broad software-style rendering evaluation. Its horizontal counter now feeds an immutable package-local decoder table representing the fixed internal enable lines for background fetch/shift, sprite evaluation/fetch, scroll transfers and visible pixel clocks.

- the 341-dot horizontal schedule is decoded once into immutable chip-local timing lines; normal execution indexes that retained decoder rather than rebuilding dot ranges/modulo phases on every PPU clock;
- rendering circuitry runs when either background or sprite rendering is enabled, matching the physical PPU where disabling one layer hides that layer but does not stop the shared rendering sequencer; forced blank disconnects both fetch and OAM evaluation circuits;
- sprite evaluation is active only on visible scanlines; the pre-render scanline retains the prior secondary-OAM result for its sprite-fetch phase;
- VRAM package outputs are driven by transaction-state transitions themselves: address/ALE phase when a transaction begins, data `/RD` or `/WR` phase on the next internal phase, and idle only when the transaction actually completes without an immediately following fetch;
- the vblank latch directly drives the package-local NMI gate when the latch or PPUCTRL NMI-enable line changes, removing the old per-dot NMI polling call;
- the visible palette path uses a predecoded physical palette-mirror index and no longer re-runs the palette mirror test for every rendered pixel;
- RP2C02 and RP2C07 share the same physical dot decoder while retaining their real NTSC/PAL raster and color-emphasis differences.

This is still the same physical hardware model: every master clock edge reaches the package pin, every external VRAM bus transition crosses package pins and motherboard traces, and the motherboard has no rendering, `/CS`, edge or receiver-interest semantics. The change is inside the chip: retained counters and latches directly select the circuitry that reacts.
