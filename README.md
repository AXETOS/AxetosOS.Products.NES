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

## v1.5.1 RP2C0x parallel background shifter core

v1.5.1 follows the v1.5.0 profile inside the physical RP2C02/RP2C07 packages. The hardwired decoder reduced the cost of package-output, sprite, raster and video-output work, while background shifting/pixel extraction and VRAM transaction handling became the dominant named PPU sections. This release keeps the v1.5 hardwired timing architecture and reduces host work in the background circuitry without changing any externally observable package timing.

- the four physical 16-bit background shift-register lanes (pattern low/high and attribute low/high) are retained in one packed 64-bit host word, with lane-boundary masking so one host shift clocks all four hardware lanes in parallel;
- the next-tile pattern and attribute fill state is packed in the same lane layout, making the hardware load edge one masked merge instead of four independent field updates;
- fine-X now retains the corresponding shifter tap when PPUSCROLL changes, so visible pixels sample the already-decoded tap instead of rebuilding `0x8000 >> fineX` on every pixel;
- the hot VRAM address/data phase helpers and background shift/load/pixel helpers are explicitly inlined;
- RP2C02 and RP2C07 remain behaviorally separate physical packages with the same external pin timing and region-specific raster/emphasis behavior.

No motherboard behavior changes. Every PPU clock, VRAM address/data transition, ALE, `/RD`, `/WR`, and package-pin delivery remains physical and immediate. The packing is only an internal host representation of four independent chip-local shift registers.

## v1.5.2 RP2C0x hardwired rendering VRAM fetch circuit

v1.5.2 separates the PPU's fixed rendering fetch circuit from the slower CPU `$2007` VRAM read/write sequencer. Background and sprite rendering reads are always the same physical package transaction: address/ALE, then released AD with `/RD`, then capture. The hot renderer no longer carries generic transaction kind, completion-policy, or CPU-read/write purpose state through every fetch.

- rendering fetches retain only their physical address, two-phase read state, and destination latch;
- the rendering address phase drives A8-A13, AD0-AD7 and ALE immediately when the internal dot decoder fires;
- the next PPU dot directly drives the read/data phase with AD released and `/RD` asserted;
- the following dot samples AD and routes the returned byte directly to the selected nametable, attribute, pattern, or sprite latch;
- CPU `$2007` reads/writes keep their separate three-phase physical transaction path and cannot overlap an active rendering fetch;
- `VramTransactionActive` now represents either physical internal VRAM sequencer owning the package bus;
- no external A/AD/ALE/`/RD`/`/WR` transition is removed or coalesced.

This keeps the dumb-motherboard boundary unchanged. The optimization is entirely inside RP2C02/RP2C07: fixed-function rendering circuitry no longer pays generic software transaction bookkeeping designed for the CPU port.


## v2.0.0 startup-compiled physical Famicom/NROM machine

v2.0.0 changes the execution architecture rather than continuing the v1.x sequence of small hot-path optimizations. The existing `VirtualHardware` board/chip/pin/net model remains the authoritative physical definition. After a mapper-0 cartridge is physically attached to the Japanese Famicom board and topology validation completes, the fixed machine is compiled into a dedicated runtime fabric for normal execution.

The first compiled machine covers the benchmark-critical fixed hardware paths:

- the one-driver `MASTER.CLK` trace is compiled directly to the RP2A03 and RP2C02 package clock pins while retaining every physical Low/High presentation and each chip pin's own divide-by-six/divide-by-four activation counter;
- CPU A0-A15 are compiled as one 16-trace parallel route;
- CPU D0-D7 are compiled as one eight-trace shared electrical route with the real RP2A03, PPU, work-RAM and cartridge drivers still participating in per-bit Hi-Z/strength/contention resolution;
- PPU AD0-AD7 are compiled as one eight-trace shared electrical route across RP2C02, SN74LS373, CIRAM and the cartridge;
- PPU A8-A13 are compiled as one six-trace physical route;
- the SN74LS373-to-CIRAM A0-A7 path is compiled as one eight-trace physical route;
- package output batching remains atomic: a chip's complete output state is established before any receiving package executes, even when one reaction changes both compiled buses and ordinary scalar control pins;
- receiver pin levels are still stored unconditionally before chip-owned activation gates decide whether internal circuitry wakes;
- no signal queue, scheduler, motherboard `/CS` knowledge, direct CPU-to-ROM call, skipped PPU/CPU clock, or receiver-aware transport suppression is introduced.

The desktop host uses the compiled Famicom/NROM machine by default. `--reference-runtime` disables the startup-compiled routes in the same build so the old per-trace runtime can be benchmarked against the compiled machine without changing ROM, chip implementation, rendering, audio, or board topology.

This release intentionally starts with Famicom + NROM because Super Mario Bros. and Donkey Kong provide a controlled mapper-0 performance baseline. NTSC NES, PAL NES and later mapper hardware remain on the existing physical runtime until their compiled forms are validated.


## v2.1.0 fused compiled Famicom/NROM circuit

v2.1.0 retires the v2.0 per-bus route experiment. That design still fed the ordinary `DigitalPin`/`DigitalNet`/package execution graph and benchmarked slower than the reference runtime.

The new Famicom + NROM compiled runtime folds the fixed machine one level deeper. The ordinary physical board is still assembled and topology-validated first, but normal execution then bypasses motherboard component dispatch for the benchmark machine:

- the 12-master-cycle RP2A03/RP2C02 divider schedule is precompiled and executed directly;
- full master cycles are accounted without publishing clock levels that cannot activate internal chip state;
- CPU RAM mirroring is resolved directly by the compiled circuit;
- NROM 16/32 KiB PRG selection is fixed at startup;
- CPU $2000-$3FFF register selection reaches the RP2C02 register core directly through the compiled decoder;
- PPU pattern fetches reach NROM CHR directly while preserving the retained two-dot PPU fetch phases;
- nametable accesses reach CIRAM directly through startup-fixed horizontal/vertical mirroring;
- PPU /NMI reaches the RP2A03 edge latch directly;
- CPU/PPU package pins, motherboard nets, LS139, LS373, LS368, SRAM package evaluation and NROM package evaluation do not participate in the normal compiled hot loop.

The chip classes still retain the authoritative CPU/PPU/APU silicon state and their standalone pin-driven paths remain available for package conformance tests. `--reference-runtime` still runs the v1.5-style physical per-trace engine from the same executable for A/B comparison.

## v2.1.1 real-time host pacing and fused APU conformance

v2.1.1 keeps the v2.1 fused Famicom/NROM circuit and changes the native desktop host from throughput-driven presentation to hardware-clock pacing by default. The compiled machine may have more than 60 FPS of host headroom, but normal play now advances emulated time against the real 21.477272 MHz NTSC/Famicom master clock instead of allowing the game and PCM producer to run ahead of wall time.

- normal desktop execution is paced from accumulated master cycles, not from an arbitrary 60 FPS cap;
- `--uncapped` preserves raw host-throughput benchmarking, while `--profile` remains uncapped automatically;
- the host prints final APU cycle, DAC-event and DAC-level diagnostics so a silent title can be distinguished from an audio-device/pacing problem;
- a new Famicom/NROM conformance ROM programs RP2A03 pulse 1 through `$4000`, `$4002`, `$4003` and `$4015`, then requires the compiled and reference runtimes to produce the same non-zero DAC sample sequence.

The fused circuit itself is unchanged in this release. The purpose is to make >60 FPS execution usable as real hardware headroom rather than faster-than-hardware game time, and to close the APU-only coverage gap exposed by the first v2.1 game tests.

## v2.2.0 true-hardware electrical hot-path sweep

v2.2.0 begins the post-v2.1 optimization program on the real pin/net/package runtime rather than adding more NES-specific fused behavior. The board remains topology-only and every chip remains isolated from peer components and wiring semantics.

The electrical layer restores the proven direct/unrolled three- and four-driver resolvers used by the faster v1.4.2 physical runtime, removing the later incremental per-driver bookkeeping from common shared buses. It also topology-compiles the dominant ordinary input-only and bidirectional `AnyChange` package-pin acceptance paths so each delivered physical level no longer reinterprets pin direction and activation mode before the chip-owned wake gate is consulted.

No physical delivery, Hi-Z state, drive-strength rule, contention state, package-boundary atomic output publication, or receiver activation rule is removed. The v2.1 fused Famicom/NROM runtime remains available only as the existing experimental/fallback path; this release's performance work targets the generic hardware-lab execution path.

## v2.5.0 aggressive true-hardware package/electrical sweep

v2.5.0 is an intentionally aggressive performance experiment built from the validated v2.2 true-hardware baseline. It does not use the fused Famicom/NROM shortcuts and intentionally rolls back the v2.3 package-aggregation and v2.4 discrete-chip experiments.

The package boundary now stages changed physical pins in a 64-bit package-pin mask rather than a per-reaction reference array/publication sequence. Current NES packages fit in one mask; arbitrary laboratory packages above 64 pins retain a generic overflow path. A changed bit still represents one real physical package pin and is still published through that pin's own attached `DigitalNet`.

`DigitalBus` adds package-local parallel output staging for 6-, 8- and 16-pin buses when all member pins belong to the same chip reaction. Every pin's actual drive state changes individually, but the package records the resulting physical pin-change mask once instead of re-entering staging logic for every bit. Outside a chip reaction the ordinary per-pin immediate path is unchanged.

Digital output level and drive strength are packed into one byte per `DigitalPin`, and the 2/3/4-driver electrical resolvers consume that packed state directly. High/Low/Hi-Z/Unknown, weak/strong priority and contention semantics are unchanged. `DigitalLevel`, `DigitalDriveStrength` and `PinDirection` also use byte-sized enum storage.

The architecture remains lab-safe: chips know only themselves, boards know only physical connections, arbitrary rewiring still changes behavior, and no NES/CPU/PPU/mapper meaning is present in the generic electrical runtime. Two package-boundary tests verify wide-bus atomicity and final-state publication when a physical output changes more than once during one chip reaction.

The previously validated v2.2 suite contained 226 tests. v2.5.0 adds two tests, so the expected total is **228**.

## v2.6.0 RP2C0x packed internal execution sweep

v2.6.0 keeps the v2.5.0 true-hardware electrical/package baseline and aggressively optimizes only the RP2C02/RP2C07 package-internal execution representation. The motherboard, physical package pins, external VRAM bus, CPU register bus, NMI output and clock ownership are unchanged.

- Replaces broad per-dot flag interpretation with one immutable packed 341-dot internal decoder word per PPU dot.
- Packs each of the eight retained sprite output units into one 64-bit state word while preserving independent tile, attribute, X counter, row, pattern and sprite-zero state.
- Uses byte-reversal lookup for horizontally flipped sprite pattern fetches.
- Moves rare vblank/pre-render raster decoding off the ordinary horizontal-counter path.
- Uses the same RP2C0x core in both the true-hardware and fused compiled execution modes, so any measured core gain benefits both.
- No chip learns what board, memory, cartridge or other chip is attached to it. All external communication remains package-pin electrical behavior.

The standalone/conformance suite remains 228 tests; runtime performance must be validated locally before this experiment is accepted as a new baseline.

## v2.10.0 packed electrical truth-table resolver experiment

v2.10.0 is a clean, drastic replacement experiment built from the validated v2.6 baseline. It deliberately retires the v2.8 optional generic transport and the v2.9 package-fanout experiment rather than layering another execution mode beside them.

The active true-hardware runtime replaces the common 2/3/4-driver shared-net arbitration kernel itself. Each physical output pin still owns its individual drive level and strength, but topology compilation assigns each driver on a common shared net one anonymous four-bit lane. A changed driver updates that lane immediately. Net resolution then uses one immutable 65,536-entry electrical truth table instead of repeatedly loading multiple driver objects and branching over strength, Hi-Z, unknown and contention rules.

Unused lanes on two- and three-driver nets are compiled as strong Hi-Z, allowing the same four-lane truth table to execute all common shared-net cases without a runtime driver-count branch. Single-driver traces retain their existing direct path, while laboratory nets with more than four drivers retain the generic cold resolver.

This remains topology-only hardware simulation: the electrical layer knows physical pins, wires, driver slots, drive strength and resolved levels. It has no NES, CPU, PPU, RAM, cartridge, mapper, address-space or board-signal semantics. Arbitrary rewiring, Hi-Z, weak/strong priority and output contention remain observable physical behavior.

The suite adds an exhaustive four-driver electrical conformance test over every legal Unknown/Low/High/Hi-Z × weak/strong combination, increasing the expected test total from 228 to **229**.
