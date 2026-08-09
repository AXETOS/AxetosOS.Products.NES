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
- PPU AD0-AD7 are compiled as one eight-trace shared electrical route across RP2C02, the SN74LS373 D inputs, CIRAM data and the cartridge PPU-data pins;
- PPU A8-A13 are compiled as one six-trace physical route;
- the SN74LS373 Q0-Q7 low-address path is compiled as one eight-trace physical route to CIRAM A0-A7 and the cartridge PPU-address pins;
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


## v2.11.0 whole-circuit compiled laboratory motherboard experiment

v2.11.0 introduces a new `--compiled-lab` execution architecture while preserving both the v2.10 true-hardware reference runtime and the existing hand-fused Famicom/NROM runtime. The goal is to test whether the hardware laboratory itself can compile a fixed assembled motherboard into a much faster executable circuit without teaching the compiler NES address-space or game semantics.

The compilation boundary deliberately stops at the cartridge connector. The fixed motherboard is compiled as one runtime unit; the inserted cartridge/mapper is represented by a second replaceable runtime unit. ROM contents and mapper-specific behavior therefore remain outside the motherboard compiler and can change without redefining the fixed board.

The motherboard compiler derives optimizations from physical chip definitions and the actual assembled netlist:

- it identifies the installed RP2A03, RP2C02 and SRAM packages by physical chip type, then determines the CPU-side and PPU-side SRAM roles from their real shared data traces rather than component IDs;
- it evaluates the connected SN74LS139A and SN74LS368A combinational circuitry for all RP2A03 address/RW source patterns and emits 65,536-entry read/write dispatch tables for the exact assembled board;
- it derives SRAM and RP2C02 register address projections from actual package-pin connectivity;
- it traces RP2C02 AD pins through the installed SN74LS373 into the PPU-side SRAM and the replaceable cartridge low-address boundary, while requiring the externally supplied CIRAM select/address contribution to originate at the replaceable-device boundary;
- it derives the RP2A03/RP2C02 master-clock activation periods and shared-edge ordering from their physical clock pins. When the assembled circuit proves a 6/4 repeating schedule, it emits the existing unrolled 12-master-edge execution kernel as a circuit-derived shortcut;
- it directly fuses PPU NMI delivery only after proving that the RP2C02 and RP2A03 NMI package pins are on the same physical trace;
- it validates direct PPU-side SRAM `/OE` and `/WE` shortcuts against the actual connected control traces before compiling them.

No motherboard route contains hardcoded `$0000-$1FFF`, `$2000-$3FFF`, `$8000-$FFFF`, "CPU RAM", "PPU register", NROM, mapper, or game rules. Mapper-0 PRG/CHR and mirroring behavior lives only in the replaceable NROM cartridge runtime unit. A future MMC1/MMC3 cartridge can therefore replace that unit without changing the fixed-motherboard compiler.

The existing default fused Famicom/NROM runtime remains unchanged and available as the proven ~60 FPS performance fallback. `--reference-runtime` continues to select the v2.10 physical pin/net runtime, and `--compiled-lab` selects this new whole-circuit compiler experiment. These modes are mutually exclusive so benchmark results do not include duplicate routing engines.

Three new conformance tests verify the compilation boundary, exact machine-state equivalence with the physical reference at the same master-cycle boundary, and APU/DAC equivalence. The expected suite total is **232** tests.


## v2.12.0 product-agnostic whole-circuit hardware compiler

v2.12.0 generalizes the v2.11 whole-board performance breakthrough into a hardware compiler whose optimization input is only component-provided hardware facets plus the assembled physical netlist. The existing hand-fused Famicom/NROM runtime remains available unchanged as the default proven fast path, while `--compiled-lab` uses the generalized compiler.

The compiler no longer searches for RP2A03, RP2C02, HM6116, 74-series, controller, cartridge, Famicom, NROM, CPU-RAM, CIRAM, or other product roles. Instead, components may advertise generic physical capabilities such as bus-master pins, addressable target pins and select conditions, combinational output truth, bit projection, clock-source/clocked behavior, signal sinks, serial peripherals, and replaceable external-device boundaries. Those contracts are public so new/custom lab chips can participate without modifying compiler source.

At startup the compiler derives the executable circuit from hardware facts only:

- bus targets are associated through actual shared data traces;
- target address bits are projected from actual physical address wiring, including bit permutations;
- data-line permutations are compiled from real wiring rather than assuming D0-D7 order;
- target selection is obtained by recursively evaluating the connected components' own combinational hardware contracts;
- clock-domain periods and same-edge execution order come from actual clock pins and their physical net order;
- serial peripherals and signal sinks are bound by shared physical traces;
- replaceable external hardware remains a separate runtime unit while still exposing ordinary hardware facets at the connector boundary;
- an unmodelled output-capable data-bus driver now prevents compilation instead of being silently ignored.

The compiler may still collapse, precompute, fuse and eliminate intermediate runtime work as aggressively as it can prove safe. What it may not use is product meaning. Renaming a board, component or net therefore does not create an optimization, while changing a chip or wire forces the affected routes and shortcuts to be rediscovered from the new circuit.

As a non-NES proof, the existing Tiny8 pin-driven example computer now exposes the same generic compiler facets. A new conformance test compiles and executes Tiny8 + generic RAM + program ROM + binary decoder + inverter through the same whole-circuit compiler, proving that the compiler path is not tied to the NES motherboard classes.

Expected test total: **233**.

## v2.12.1 generic signal-router type hotfix

v2.12.1 fixes a compile-time generic type-inference issue in the product-agnostic whole-circuit compiler. `CompiledSignalRouter` now groups physical signal sinks directly by `DigitalNet`; `DigitalNet` already uses reference identity, so this preserves the intended hardware semantics while keeping the compiler contract strongly typed. No execution behavior, NES/product knowledge, mapper behavior, or optimization strategy is changed from v2.12.0.

## v2.13.0 replaceable cartridge hardware units and MMC1 proof

v2.13.0 makes the cartridge boundary genuinely independent from the product-agnostic fixed-motherboard compilation. In `--compiled-lab`, the Famicom motherboard and its fixed chips can now be compiled before a ROM is loaded. ROM metadata is interpreted only by the cartridge composition factory, which constructs the matching replaceable cartridge hardware and physically inserts it through the existing connector nets. Cartridge replacement binds/unbinds a separate compiled external unit without recreating the motherboard execution plan.

The generic compiler now excludes every `ICompiledExternalDevice` from fixed-unit bus targets, clock/reset discovery, signal-sink discovery and bit-projection proofs. Fixed-board targets whose electrical selection or address depends on an external connector driver are retained as dynamic boundary targets and resolved from the live physical topology. This is required for cartridge-controlled CIRAM `/CE` and A10 wiring while keeping the board compiler ignorant of mapper semantics.

Mapper 1 / MMC1 is added as the first bank-switched cartridge proof. The cartridge owns its serial load register, control/CHR/PRG bank state, PRG RAM, PRG/CHR banking and CIRAM A10 mirroring output. The same ROM-side factory continues to construct NROM for mapper 0. The motherboard compiler contains no mapper switch, cartridge address-map names or product-specific mapper rules.

The architectural regression suite now proves that one compiled Famicom motherboard keeps the same compilation identity across NROM eject and MMC1 insertion, and adds an MMC1 execution ROM that performs real five-write serial PRG bank selection before comparing compiled execution with the physical reference path. Standard MMC1 up to the base 256 KiB PRG addressing model is the scope of this first proof; later SxROM board variants, revision-specific PRG-RAM control and consecutive-cycle write suppression remain separate cartridge-hardware refinements.


## v2.13.1 MMC1 physical write-strobe hotfix

v2.13.1 keeps the v2.13.0 replaceable-cartridge architecture unchanged and fixes the physical MMC1 reference path. MMC1 CPU transactions are now latched on the cartridge package's falling `M2` edge, when the active address, R/W and data levels are still physically present. This matches the RP2A0x package model's atomic output publication: its rising `M2` transition and next-cycle bus outputs are published together, so using the rising edge inside the cartridge could observe the newly-started cycle and miss the completed write. The compiled cartridge unit and fixed motherboard compiler remain mapper/product agnostic.

The boot-host unsupported-mapper regression now uses mapper 2 because mapper 1 is intentionally supported by the new MMC1 cartridge hardware.

## v2.13.2 MMC1 PPU bus ownership and consecutive-write conformance

v2.13.2 keeps the validated replaceable-cartridge architecture and corrects two cartridge-local MMC1 behaviors exposed by real-ROM testing. During the RP2C0x address phase, MMC1 now releases the multiplexed PPU AD0-AD7 pins when ALE rises before latching the low address byte. This prevents the previous CHR read value from electrically contending with the PPU's next address. NROM already followed this connector rule.

**Historical note (superseded by v2.23.0):** the v2.13.2 cartridge-side ALE/AD model treated the PPU package's multiplexed bus as though it appeared directly on the cartridge connector. v2.23.0 corrects that physical topology: the console motherboard's SN74LS373 owns low-address demultiplexing, and replaceable cartridges receive separate PPU A0-A13 and D0-D7 connector buses. The CPU-side consecutive-write fix described below remains valid.

MMC1 also models its consecutive CPU write-cycle filter. The cartridge remembers M2/RW bus-cycle history and ignores D0 serial writes that immediately follow another write cycle, while bit-7 reset remains effective. The compiled path obtains the same behavior through a generic bus-target cycle-observation facet associated by actual bus topology; the compiler contains no MMC1/mapper/product or address-map knowledge.

The desktop host now prints final MMC1 control, CHR-bank and PRG-bank registers together with mapper-write, ignored-consecutive-write and PPU-read counts. Three conformance tests cover ALE bus release, the serial-write filter/reset exception and a real 6502 RMW double-write sequence across compiled and physical execution. Expected suite total: **250**.

## v2.13.3 MMC1 physical-bus regression fixture hotfix

v2.13.3 changes no cartridge, motherboard or compiler behavior from v2.13.2. The new standalone MMC1 PPU electrical regression now instantiates `VirtualHardwareSimulator` after wiring its synthetic laboratory board so those traces are topology-compiled before the test drives ALE/RD/AD sources. Without that normal board initialization step, `DigitalSignalSource.Set` correctly changed package drive state but the uncompiled fixture nets could not propagate, causing the test to fail before it exercised MMC1 at all. Expected suite total remains **250**.

## v2.13.4 cartridge RAM topology and deterministic MMC1 A/B diagnostics

v2.13.4 tightens the replaceable-cartridge hardware description after real MMC1 ROM testing. NES 2.0 cartridge images now retain their explicit volatile/nonvolatile PRG and CHR RAM capacities from header bytes 10 and 11 instead of silently falling back to a generic MMC1 RAM assumption. Legacy iNES images keep their documented compatibility inference, while directly constructed laboratory images keep the existing unknown/legacy defaults.

MMC1 now constructs a CPU $6000-$7FFF RAM target only when the image describes an actual 8 KiB PRG RAM/NVRAM device (or when a legacy image requires the compatibility assumption). A cartridge with zero NES 2.0 PRG RAM therefore leaves that connector address range electrically un-driven. For cartridges that do contain the RAM device, the MMC1 PRG register's package-local RAM-enable state is exposed through a generic dynamic target-selection facet. The whole-circuit compiler treats that facet only as component-provided hardware behavior; it contains no mapper, cartridge, board, product or address-map rule. Unsupported larger SxROM RAM topologies are rejected rather than guessed.

The desktop host adds `--stop-frame N` for deterministic physical-vs-compiled comparisons. Final MMC1 diagnostics now include RAM presence/enable, serial commit/reset counts, the last mapper write and an FNV-1a hash over the complete mapper-write address/data stream. Running both runtimes to the same PPU frame therefore reveals whether they received identical cartridge transactions. ROM startup diagnostics also print parsed header/submapper and PRG/CHR RAM/NVRAM capacities.

Three new conformance tests cover NES 2.0 RAM-size decoding, physical absence of an MMC1 PRG-RAM target, and mapper-local dynamic RAM chip-enable behavior. Existing compiled/reference MMC1 execution tests additionally compare the mapper write-stream hash. Expected suite total: **253**.

## v2.14.0 RP2C0x deterministic video-state conformance

v2.14.0 moves the real-ROM correctness investigation past the cartridge boundary. Alien Syndrome's v2.13.4 same-frame A/B run reached PPU frame 500 with identical master cycles, CPU state, APU counters, MMC1 registers, complete mapper-write stream hash and cartridge PPU-read count. The next diagnostic boundary is therefore the RP2C02 itself and the completed video frame, not mapper semantics.

The RP2C02 now exposes an inspection-only deterministic snapshot of retained chip circuitry: CPU-facing register latches, v/t/x/w scrolling state, background tile/attribute/fetch latches, packed background shifters, sprite pipeline state, palette/OAM hashes, current pixel/color state and PPU bus transaction counters. CIRAM exposes a corresponding inspection hash, and DesktopHost prints those hashes together with an FNV-1a hash of the completed 256x240 ARGB frame. None of these hashes feeds execution or changes the compiled hot path; they exist only to compare two physical executions stopped at the same master-clock boundary.

The shared RP2C0x horizontal decoder also gains the two real nametable bus reads performed at dots 337-340 of each rendering scanline. Their returned bytes are intentionally discarded by the pixel pipeline, but the address/read activity remains physically visible at the PPU package and cartridge connector. RP2C02 and RP2C07 both execute these bus cycles from the same chip-local decoder.

The existing compiled/reference MMC1 conformance test now also requires exact RP2C02 diagnostic-snapshot and CIRAM-state equality. Three new standalone tests cover the two end-of-scanline nametable reads on NTSC/PAL PPUs and verify that RP2C02 state inspection observes palette/OAM changes without mutating execution.

Expected test total: **256**. Local `dotnet test` remains the acceptance gate.

## v2.15.0 RP2C0x delayed CPU VRAM address-generator timing

v2.15.0 corrects a deterministic RP2C0x timing error exposed after v2.14.0 proved that the physical reference and whole-circuit compiled paths reach identical CPU, mapper, CIRAM, OAM, PPU-pipeline and framebuffer state. The remaining artifact therefore belongs to the shared PPU hardware model rather than the compiler or cartridge boundary.

CPU register accesses no longer mutate the internal PPU VRAM address generator asynchronously on the CPU package edge. The second `$2006` write updates the temporary address latch immediately but reaches the live `v` address through a three-PPU-edge internal path. `$2007` reads/writes still perform their existing package memory transaction, but their address increment is now retained as a chip-local delayed edge and clocks `v` six following PPU edges later. Because the CPU transaction can occur between PPU edges, this models the measured five/six-dot propagation interval rather than imposing an NES-level scheduler.

The delayed `$2007` edge is applied before the hardwired rendering decoder for that dot. This matters when it lands on dot 257: horizontal increment can occur electrically and the dot-257 `hori(t) -> hori(v)` reload then wins for the horizontal bits, matching the physical address-generator collision instead of the previous immediate software-style mutation.

The same address-generator circuitry is implemented in RP2C02 and RP2C07. It is entirely chip-local; no game, mapper, board, address-range or product semantics were added to the motherboard or whole-circuit compiler. DesktopHost now reports delayed `$2006` commit and `$2007` increment counts so real-ROM runs can confirm that the hardware path is being exercised.

Four new standalone conformance tests cover NTSC/PAL delayed `$2006` transfer timing and the dot-257 delayed `$2007`/horizontal-reload collision. Existing PPUDATA package-bus tests now separately verify that the external memory transaction can complete before the internal `v` increment reaches the address generator.

Expected test total: **260**. Local `dotnet test` remains the acceptance gate.


## v2.16.0 MMC1 cartridge-video correlation trace

Alien Syndrome exposed deterministic tile corruption that does not occur in the validated Mapper 0 titles. v2.16.0 therefore adds a diagnostic-only cartridge/PPU correlation path before making another speculative hardware change.

`--cartridge-video-trace START:END` observes the RP2C02's actual rendering fetch completions and asks the inserted MMC1 cartridge how each physical PPU address maps to CHR ROM or CIRAM at that exact moment. The trace records mapper-visible register changes at the current PPU frame/scanline/dot and emits compact per-frame signatures for CHR/CIRAM mapping plus four generic 60-scanline framebuffer bands.

The observer is disabled outside the requested frame window and is not used by normal execution, the motherboard, or the whole-circuit compiler. MMC1 exposes inspection-only mapping/register diagnostics; the physical hardware behavior remains unchanged.

Example focused capture:

```powershell
dotnet run -c Release --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost -- "C:\ROMs\game.nes" --board famicom --compiled-lab --uncapped --cartridge-video-trace 1450:1850
```

If no explicit `--stop-frame` is supplied, a cartridge-video trace stops automatically immediately after its requested window.

## v2.16.1 diagnostic output-boundary hotfix

v2.16.1 preserves the v2.16.0 MMC1 cartridge-video trace but removes the two host callback references that the first diagnostic implementation accidentally retained inside physical hardware components. RP2C02 rendering-fetch diagnostics and MMC1 register diagnostics now leave their owning hardware through package-owned `BufferedOutputPin<T>` diagnostic outputs. DesktopHost enables and drains those outputs only inside the requested trace window.

This changes no PPU, mapper, motherboard, cartridge-bus or compiler behavior. It restores the architectural rule that concrete hardware packages retain no board, simulator, peer-package or callback references. The expected suite remains **262** tests.

## v2.16.2 diagnostic test consumer hotfix

v2.16.2 completes the v2.16.1 diagnostic-boundary migration by updating the MMC1 regression test itself to consume `RegisterTraceOutput` through `BufferedOutputPin<T>` capture/drain semantics. The stale test-side reference to the removed `RegisterDiagnosticObserver` callback is gone.

Production RP2C02, MMC1, cartridge, motherboard, electrical and compiler behavior is unchanged from v2.16.1. The expected suite remains **262 tests**.

## v2.16.3 precise MMC1 raster-correlation trace

v2.16.3 tightens the diagnostic-only cartridge-video trace after the first Alien Syndrome capture exposed two important facts: MMC1 control/mirroring, CHR bank 0 and PRG bank remain stable through the affected frame window, while CHR bank 1 changes twice per frame. The original v2.16 trace could not place those commits precisely because DesktopHost normally advanced 16,384 master clocks between diagnostic drains, so buffered mapper events were stamped with a PPU raster position several scanlines after the physical commit.

While `--cartridge-video-trace` capture is active, DesktopHost now advances and drains diagnostics every 12 master clocks, one CPU bus-cycle period on the Famicom clock tree. Normal execution remains on the existing 16,384-master-clock presentation batch. The smaller diagnostic cadence changes observation only; the motherboard, CPU, PPU, mapper and whole-circuit compiler execute the same physical clocks.

Each `Mmc1RegisterTraceEvent` now carries the cartridge-local PPU read/write counters captured at the exact serial-register commit. `CART MMC1` records print those exact counters together with the externally observed raster and the number of PPU reads that occurred before the trace was drained. This makes the remaining raster observation error explicit instead of silently presenting a coarse drain-time timestamp as the commit time.

Per-frame trace output is also held until the RP2C0x frame counter advances. This keeps the real pre-render scanline fetches in the same frame summary and removes the duplicate 137-fetch `CART FRAME` records produced after the framebuffer had completed but before the PPU entered the next frame.

No MMC1, RP2C0x, motherboard, cartridge-bus or compiler execution behavior is changed. Expected suite total remains **262 tests**. Local `dotnet test` remains the acceptance gate.



## v2.17.0 MMC1 end-of-cycle write timing

The precise Alien Syndrome A/B capture isolated a compiled-boundary timing error rather than a mapper-register equation error. Reference and whole-circuit compiled execution commit the same MMC1 CHR1 values at the same CPU transaction sequence, but on collision frames the compiled path committed the fifth serial write one PPU fetch too early. The physical cartridge samples CPU writes on the falling M2 edge; the compiled bus previously invoked every target write immediately when the RP2A03 began the write cycle on rising M2.

v2.17.0 extends the product-agnostic compiler contract with a generic write phase (`Begin` or `Complete`). The compiled bus retains the address/data of a write cycle and can present completion-phase targets when the bus master announces the physical cycle-completion edge. RP2A03 now announces completion on its chip-local falling M2 half-cycle. MMC1 CPU-side targets request `Complete`, matching the package model's existing falling-M2 latch. No mapper number, cartridge role, Famicom identity or address-map semantics are added to the compiler. Other targets retain the existing begin-phase behavior unless their own hardware facets request otherwise.

This specifically addresses the captured frames where reference performed one final PPU CHR read from the old bank before the MMC1 commit while compiled execution switched the bank before that same read. Two regressions cover the MMC1 target's completion-phase contract and repeated rendering-time CHR1 commits landing on the same cartridge PPU-read boundary in physical and compiled execution.

Expected test total: **264**. Local `dotnet test` remains the acceptance gate.

## v2.18.0 live MMC1 CHR output and end-of-read sampling

v2.18.0 moves the Alien Syndrome investigation from compiled-vs-reference parity to the shared physical cartridge behavior. v2.17.0 made MMC1 CPU writes land on the same falling-M2 boundary in both paths, but the visible artifacts remained because both paths still effectively retained the CHR byte selected when the PPU read window began.

MMC1 CHR bank outputs are mapper-controlled address lines feeding the cartridge character ROM. When a serial-register commit changes CHR0/CHR1 while a PPU `/RD` window is already active, the selected ROM address therefore changes without requiring another PPU ALE or `/RD` edge. The physical MMC1 package now re-drives an already-active CHR read immediately from the newly selected bank while preserving the same transaction/read count. Control-register changes likewise refresh the mapper-controlled CIRAM outputs from the currently presented PPU address.

The compiled RP2C02 path now keeps each external VRAM read open across the same two-dot package transaction used by the physical PPU: `BeginRead` occurs when the data phase is asserted and `CompleteRead` resolves the byte only at the PPU sample phase. This allows replaceable hardware state to change during the read window instead of freezing the memory byte at read assertion. CPU-side PPUDATA reads use the same split begin/sample behavior.

To preserve package-local edge accounting independently from late data resolution, `CompiledBusTargetDescriptor` gains an optional generic selected-read-begin observer. MMC1 uses it only to retain its physical PPU-read counter semantics; the compiler assigns no mapper, cartridge, product or address-map meaning to the callback.

Two new regressions cover an active physical MMC1 CHR read changing immediately when CHR1 commits without another PPU edge, and the compiled PPU target observing read assertion separately from final data resolution. The rendering-time MMC1 A/B test additionally requires full RP2C02 diagnostic and CIRAM-state equality.

Expected test total: **266**. Local `dotnet test` remains the acceptance gate.


## v2.19.0 MMC1 raster-split CPU provenance trace

The full Alien Syndrome frame 1-1850 capture shows that the game uses MMC1 CHR bank 1 as an active raster resource rather than changing it only during blanking. Early gameplay already switches CHR1 during the visible picture and restores it near the end of the frame; after the title-sequence transition the game performs two visible CHR1 changes per frame while control/mirroring and the other mapper-visible banks remain stable. The recurring visual corruption therefore tracks a real cartridge raster-bank-switch workload that Mapper 0 cartridges cannot exercise.

v2.19.0 deliberately makes no further CPU, PPU, MMC1 or compiler execution change. Instead, `--cartridge-video-trace` now correlates each PPU-visible MMC1 commit with host-observed RP2A03 timing state: CPU cycle, cycles since the most recent PPU NMI falling edge, program counter/opcode/microcycle, current physical CPU bus address/direction, completed instructions/interrupts, sprite-zero timing, and DMC/OAM-DMA activity since NMI. The host samples only existing public chip diagnostics at the already-enabled 12-master-clock precision cadence. No chip gains a peer, board, simulator or callback reference, and none of the new data feeds execution.

This provenance trace is intended to distinguish the remaining shared-model possibilities without another speculative hardware patch: an NMI/service-latency error, a sprite-zero synchronization error, DMC/OAM-DMA CPU stalls, or ordinary RP2A03 instruction-cycle timing. Once the same raster split is tied to its actual CPU-side trigger, the next correction can be made in the physical circuit that causes the phase error instead of moving mapper/PPU behavior by guesswork.

Expected test total remains **266**. Local `dotnet test` remains the acceptance gate.


## v2.20.0 MMC1 raster-trigger provenance trace

The v2.19.0 full Alien Syndrome trace narrows the title corruption substantially without changing hardware behavior. Every title-phase CHR1 commit is observed at the same RP2A03 execution site (`$F895`, opcode `$8D`), DMC contributes no reads or stalls in the captured interval, and the one OAM DMA after NMI contributes the same 256 transferred bytes on the recurring split families. Most importantly, the second CHR1 commit follows the first by exactly 8,692 CPU cycles / 4,380 completed instructions throughout the captured title phase. The moving raster position is therefore selected before the common MMC1 writer rather than being introduced by the second serial write or by mapper timing drift.

v2.20.0 remains diagnostic-only and moves provenance one level earlier. While the existing cartridge-video trace is active, `DesktopHost` now observes the physical CPU bus at the same one-CPU-cycle cadence and records mirrored PPUSTATUS (`$2002`) reads. For each MMC1 mapping commit the trace reports:

- PPUSTATUS reads since the most recent NMI as `total/sprite-zero-clear/sprite-zero-set`;
- the most recent PPUSTATUS read, including returned physical bus byte, raster, PC/opcode and age at commit;
- the last status read that observed sprite-zero clear and the first that observed sprite-zero set;
- a rolling tail of the sixteen most recent physical opcode-fetch addresses and sampled opcode bytes;
- OAM DMA explicitly labelled as transferred bytes rather than ambiguous "DMA" units.

This is sufficient to tell whether the 20/24/30-scanline first-commit families are selected by a `$2002` sprite-zero polling loop, by a different caller/branch into the common `$F895` MMC1 writer, or by CPU work that occurs after status synchronization. All correlation state remains in `DesktopHost`; no RP2A03, RP2C02, cartridge, motherboard or compiler component gains peer knowledge, callbacks or execution feedback.

No CPU, PPU, MMC1, motherboard or compiler execution behavior changes. Expected test total remains **266**. Local `dotnet test` remains the acceptance gate.


## v2.20.1 exact PPUSTATUS provenance hotfix

The v2.20.0 Alien Syndrome title-window trace identifies a discrete PPUSTATUS polling dependency rather than mapper drift. Frames whose previous visible frame produced a sprite-zero hit perform roughly 131-132 `$2002` reads and do not leave the polling path until pre-render scanline 261; the following MMC1 CHR1 commits then land around scanlines 30/107. Frames with no previous-frame sprite-zero hit perform only two reads and begin the same invariant mapper sequence around scanlines 20/96 (or the intermediate 24/100 family). The second CHR1 commit remains exactly 8,692 CPU cycles after the first.

v2.20.0 correctly found the CPU-side `$2002` read addresses and caller (`$81C1`), but its host-side data-bus snapshot occurred between compiled read phases, so returned bytes appeared as `v=??`. v2.20.1 fixes only that diagnostic blind spot. `DesktopHost` now enables and consumes the RP2C02 package's already-existing external `SplitTraceOutput`, whose `PPUSTATUS read` event carries the exact status byte produced by the PPU before read side effects clear vblank.

Each visible MMC1 commit now reports both the CPU-side bus provenance and the exact PPU-produced status provenance:

- `ppustatus-bus-from-nmi=...` retains the CPU address/PC-side polling count;
- `ppustatus-exact-from-nmi=total/sprite-zero-clear/sprite-zero-set`;
- `ppustatus-vblank-exact=clear/set`;
- `s0-at-nmi=True/False`;
- `last-ppustatus=$xx@frame:scanline:dot:s0=n:vb=n`;
- exact last sprite-zero-clear and first sprite-zero-set status events;
- `last-ppustatus-cpu=...` retains the `$81C1` CPU provenance even when the physical data bus is between readable phases.

The PPU, CPU, MMC1, motherboard and compiler execution paths are unchanged. No chip gains a reference to another chip, board, simulator or callback; the host only drains an existing package-owned diagnostic output. Expected test total remains **266**.


## v2.21.0 sprite-zero pixel provenance trace

The v2.20.1 Alien Syndrome capture proves the title-phase CHR1 raster family is selected by the retained RP2C02 sprite-zero latch. Frames entering NMI with sprite zero set perform about 131-132 `$2002` reads and leave the polling loop only when the PPU clears sprite zero during pre-render; frames entering NMI with sprite zero clear perform only two reads and begin the otherwise-identical MMC1 sequence roughly 1,165 CPU cycles earlier.

v2.21.0 changes no emulation behavior. During explicit cartridge-video capture it records sprite-zero activation, pattern fetches, transparent-background encounters, opaque overlaps, masking/X=255 rejection, sprite selection and the authoritative hit transition. `CART FRAME` also reports PPUCTRL/PPUMASK and OAM0 Y/tile/attributes/X so hit and miss frames can be compared without changing execution.

Expected test total remains **266**. Local `dotnet test` remains the acceptance gate.


## v2.22.0 exact MMC1 CHR-read provenance

The v2.21.0 title-window trace places the remaining fault on the background/CHR1 path rather than on sprite-zero evaluation. OAM0 keeps Y=$10, tile=$D0 and attributes=$20, its fetched sprite pattern hash remains identical, and missed hits are exclusively opaque sprite-zero pixels over transparent background. Because PPUCTRL=$90 selects the `$1000` background pattern table while 8x8 sprites use `$0000`, MMC1 control=$1E routes the stable sprite through CHR0=$18 but routes the background beneath it through the actively switched CHR1 `$18/$19` bank.

v2.22.0 remains diagnostic-only. MMC1 now exposes a captured CHR-read provenance event from the exact compiled PPU read-completion callback, containing logical PPU address, physical CHR ROM address, selected 4 KiB bank, returned byte and retained mapper registers. DesktopHost correlates those package-owned events with RP2C02 rendering fetches and adds a compact `s0-bg-chr` summary for scanlines 17-24: exact-read coverage, physical bank mask, CHR1 switch raster, per-bank read/hash information and an exact logical/physical/data hash.

This directly tests whether Alien Syndrome's sprite-zero background pixels are being sourced from the intended MMC1 physical CHR bank at the instant of each visible CHR1 commit. No CPU, PPU, MMC1 mapping equation, motherboard, cartridge execution or compiler behavior changes.

Expected test total remains **266**. Local `dotnet test` remains the acceptance gate.

## v2.23.0 physical cartridge PPU-bus topology correction

The v2.22.0 Alien Syndrome capture clears the simple MMC1 CHR-bank decoder of random read corruption: every RP2C02 background-pattern fetch in the sprite-zero window paired with an exact cartridge CHR-read provenance event, with `fetch=544/exact=544/miss=0`. Frames that remain on CHR1=$19 read bank 25 for the complete window, while frames whose visible raster commit changes CHR1 from $19 to $18 split deterministically between physical banks 25 and 24 at the recorded commit raster.

Source inspection exposed a lower-level hardware-model error at the replaceable cartridge connector. The RP2C0x package multiplexes low PPU address and data on AD0-AD7, but the console motherboard already contains the SN74LS373 that captures the low address during ALE. The cartridge connector must therefore receive two separate electrical groups: PPU A0-A13 (A0-A7 from SN74LS373 Q and A8-A13 directly from RP2C0x) and PPU D0-D7 (the RP2C0x AD0-AD7 data-phase nets). The previous model incorrectly connected cartridge `PPU.AD0-AD7` directly to the multiplexed PPU package nets and made every NROM/MMC1 cartridge own a second private ALE low-address latch.

v2.23.0 corrects that topology across Famicom, NTSC NES and PAL NES assemblies and the shared replaceable-cartridge slot. `IReplaceableCartridgeHardware` now exposes a 14-bit `PpuAddress` bus and an independent 8-bit bidirectional `PpuData` bus; ALE remains entirely motherboard-local between RP2C0x and SN74LS373. NROM and MMC1 sample the already-demultiplexed package address directly, their physical and compiled PPU bus targets use the same connector pins, and MMC1's existing live CHR-bank re-drive remains cartridge-local. No motherboard mapper semantics or cross-chip references are introduced.

Regression coverage now proves that PPU data and latched low-address nets are physically distinct, that the cartridge A0 pin is attached to SN74LS373 Q rather than the raw AD net, that cartridge D0 remains on the raw RP2C0x/CIRAM data trace, and that the cartridge has no attachment to motherboard ALE. The MMC1 standalone connector regression likewise exercises independent A0-A13 and D0-D7 buses and preserves active-/RD bank-remap behavior without a synthetic cartridge-local ALE latch.

Expected test total remains **266**. Local `dotnet test` remains the acceptance gate.



## v2.25.0 hardware-preserving performance sweep

v2.25.0 keeps the corrected v2.24 CPU/PPU/APU/motherboard/cartridge behavior intact while reducing host work on the now-heavier real bus stream. MMC1 predecodes its PRG/CHR address-mux bases only when registers change, and the whole-circuit compiler builds phase-specialized read-begin/read-complete/read-observer routes so a target is not revisited during a bus phase it cannot participate in. No physical fetch, dummy cycle, mapper access or package transition is suppressed. Expected discovered test total is 277; Release FPS must be measured locally before any performance gain is claimed.

## v2.24.1 compile hotfix

v2.24.1 fixes the Famicom decoder regression test added by v2.24.0 to reference the existing `DigitalPowerRail.Output` pin instead of the nonexistent `DigitalPowerRail.Rail` member. No CPU, PPU, APU, motherboard, cartridge, mapper, compiler, or runtime behavior changes from v2.24.0. Expected discovered test total remains 276.

## v2.24.0 hardware conformance sweep

v2.24.0 audits the NES hardware model by physical-device behavior rather than by individual game symptoms. Compact implementations are retained where they are pin-equivalent; shortcuts that removed observable CPU cycles, PPU OAM/fetch phases, APU state, motherboard decode or cartridge connector signals are corrected. The cartridge CPU side now exposes A0-A14 plus M2/RW `/ROMSEL` instead of an impossible A15 pin, and both NROM/MMC1 use the motherboard's M2-qualified LS139 decode.

The release also records unresolved hardware honestly: the current CIC lock model is synthetic and lacks a cartridge key-CIC counterpart, and silicon-unstable unofficial NMOS opcodes are not assigned guessed deterministic results. See `HARDWARE_CONFORMANCE_AUDIT_v2.24.0.md` and `RELEASE_NOTES_v2.24.0.md`.

Expected local test total: **276**. Local `dotnet test` remains the acceptance gate.


## v2.26.0 automatic production startup compilation

v2.26.0 makes compilation the production execution policy for the Famicom desktop/boot host instead of a mapper-specific accident of the NROM path. The physical machine is still assembled first, and no motherboard or compiler code learns mapper semantics.

- If topology has already selected a validated specialized compiled runtime, such as the existing fused Famicom/NROM runtime, the host preserves it.
- If the assembled Famicom machine has no specialized compiled runtime, the host enables the product-agnostic whole-circuit compiler before power is applied. MMC1 therefore enters normal execution through the compiled motherboard plus replaceable cartridge runtime instead of silently falling back to raw per-trace propagation.
- The cartridge remains a separate external hardware unit. Mapper/ROM behavior is still owned by cartridge hardware facets and the physical connector topology.
- `--raw-hardware` explicitly selects the uncompiled diagnostic/reference path. The older `--reference-runtime` spelling remains accepted as an alias.
- `--compiled-lab` remains available to force the generic whole-circuit compiler for A/B work, including NROM.
- The generic compiled bus now flattens fixed-board and currently attached cartridge static routes into direct dispatch tables once at cartridge bind time. This removes repeated per-transaction motherboard-set/external-set route walking while preserving the cartridge as a separately owned runtime unit; replacement rebuilds only the dispatch cache.
- Static write routes are also split into begin/complete dispatch tables, so the hot path no longer rechecks a target's write phase after topology has already proven it. Dynamic package-local select gates retain their runtime evaluation path.

This release changes the default execution policy and performs a hardware-preserving generic-dispatch optimization; it does **not** claim that the generic compiler has reached 60 FPS. Local uncapped Release benchmarking remains the acceptance test for throughput. The existing NROM fused path is deliberately retained so Super Mario keeps its validated 60+ FPS headroom while MMC1 no longer defaults to the ~19 FPS raw runtime.


## v2.26.1 patch-layout hotfix

v2.26.1 republishes the v2.26.0 automatic startup-compilation change under the actual repository layout `AxetosOS/AxetosOS.Products.NES/...`. The v2.26.0 archive placed its changed project files one directory too high, so extracting it alongside the repository did not overwrite the active DesktopHost/VirtualHardware sources. The unchanged legacy runtime banner in the local Alien Syndrome run confirmed that the old `Program.cs` was still executing.

No NES hardware semantics are changed relative to the intended v2.26.0 implementation. Normal Famicom MMC1 launch still selects the generic whole-circuit compiler automatically; NROM retains the specialized fused compiler; `--raw-hardware` remains the explicit diagnostic raw path. This hotfix includes the complete intended v2.26 source delta so it is safe to apply even when v2.26.0 never touched the repository.


## v2.27.0 bound-topology dispatch performance sweep

v2.27.0 targets the remaining throughput gap in the product-agnostic whole-circuit compiler after v2.26.1 made that compiler the normal MMC1 startup path. The validated Alien Syndrome baseline entering this release is **45.77 FPS** in Release/uncapped mode; this release does not claim a new FPS result until measured locally.

The compiler now separates replaceable-package outputs that are provably state-independent while a cartridge is attached from outputs that depend on live mapper state. That proof is generic and opt-in through `ICompiledStaticCombinationalComponent`; it does not add mapper, cartridge, board, product, or address-map meaning to the compiler. NROM exposes its fixed CIRAM routing, while MMC1 exposes only `/CIRAM-CE` (a pure function of PPU A13) and IRQ high-impedance. MMC1 `CIRAM A10` is deliberately excluded because its mirroring source changes with the live control register.

At cartridge bind time the compiled bus uses those package-owned static facets to classify dynamic target select conditions for every bus address. Proven-rejected dynamic targets are omitted from that address's hot path, and proven-selected conditions no longer re-walk the physical combinational topology at runtime. Fixed address wiring is also preprojected into a local-address base; runtime evaluates only unresolved live address bits. For MMC1 nametable accesses this retains the live mapper-controlled CIRAM A10 bit while removing reconstruction of the other fixed SRAM address lines on every fetch.

Additional host-side hot-path reductions include skipping empty begin-read bookkeeping, a direct single-target complete-read return, cached target delegates, a single bus-cycle-observer fast path, a cached compiled IRQ signal sampler, and avoiding end-of-cycle fabric calls after RP2A03 read cycles. Cartridge ejection now removes the package from the physical netlist before rebuilding the compiled binding so those topology-derived caches also represent the true post-ejection circuit. Physical reads/writes, mapper commits, PPU fetches, bus contention behavior, and package-owned state transitions are not removed or coalesced.

A regression explicitly requires MMC1's static compiled facet to expose `/CIRAM-CE` but reject `CIRAM A10`, preventing bind-time compilation from freezing live mirroring state. Based on the validated v2.26.1 suite, the expected test total is **280**. Local `dotnet test -c Release` remains the acceptance gate, followed by uncapped Alien Syndrome and generic NROM A/B benchmarks.
