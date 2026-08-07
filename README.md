# AxetosOS Products / NES

**Version: 1.3.6**


## v1.3.6 — chip-owned activation sweep

This release makes the physical package boundary explicit across every reactive virtual-hardware component: **the motherboard always delivers the resolved electrical level to every connected pin; only the receiving chip decides whether that pin change can activate internal work.** Selection, enable, power, reset and edge semantics therefore live inside the package instead of being encoded as motherboard-side selective routing.

- Removed the v1.3.5 passive-input routing shortcut. RS/RW/data/address pins remain normal routed inputs even while a package is deselected, so their sampled electrical levels are always current. `DigitalPin.InputChangeMask` is now fixed when the package pin is created rather than being mutable runtime gating state.
- RP2C02/RP2C07 own `/CS` gating internally. Ordinary CPU-port traffic returns immediately while deselected, while selected writes now wait for a valid D0-D7 byte before the transaction is consumed. This specifically targets the v1.3.5 PPU/CHR/CIRAM graphics regression without moving PPU semantics into the board.
- RP2A03/RP2A07, NROM, HM6116, SN74LS139A, SN74LS368A, SN74LS373, CIC3193/3195/3197, ROM/RAM helpers, controller, DMA, PPU helper packages, generic 6502/Tiny8, counters, decoders and tri-state buffers were reviewed for the same rule. Data/address transitions that cannot reach an enabled internal circuit now stop at a cheap package-owned activation check before bus scanning or decoding.
- Rising-edge-only devices still receive both electrical clock levels at their package pin. A falling edge records Low and stops at the chip pin; only the activating Low-to-High edge can enter package logic.
- Removed the v1.3.4 harmonic clock-cycle skip. Even a clock pulse that cannot wake CPU/PPU logic is now physically delivered High and Low through the motherboard trace; divider/edge rejection is entirely chip-owned.
- Mapper-0 keeps CPU and PPU circuitry independent internally. CPU D0-D7 is physically delivered but ignored because NROM has no CPU-write register; PPU AD0-D7 remains electrically live and is consumed only when ALE or CHR-RAM write circuitry makes it relevant.
- The bus analyzer now uses a fixed 4,096-cycle ring instead of `List.RemoveAt(0)`, eliminating repeated O(n) history shifts once diagnostics fill.
- The APU nonlinear mixer startup table is reduced from 1,015,808 byte combinations to the equivalent 31-entry pulse and 32,768-entry TND transfer tables. Runtime mixing remains event-driven, so the gray startup period no longer includes construction of the million-entry mixer table.
- Simple continuously active hardware was deliberately left simple: an inverter reacts to each input transition; pull resistors and power-on reset react to their only relevant electrical input. No artificial enable condition was invented merely for optimization.

The governing hot-path rule is now:

```text
motherboard: deliver 0/1 to the connected pin
chip pin:    retain the physical level
chip:        if required power/select/enable/edge condition is inactive -> return
             otherwise run only the internal circuit that can be affected
```

There is still no signal queue, component scheduler, runtime settle pass, or motherboard knowledge of CPU/PPU/RAM/cartridge semantics.


## v1.3.5 — gated retained-state hot paths

> Superseded by v1.3.6: the passive motherboard-route suppression introduced here was removed; activation gating now belongs exclusively to chips.

This release moves optimization away from master-clock dispatch and into the physical CPU/PPU memory paths that remained hot in the v1.3.4 measurements. The motherboard remains queue-free and semantically ignorant: traces still present resolved electrical levels directly to package pins, while packages now avoid executing internal circuits that their real enable/control pins have disconnected.

- Passive input presentation keeps a package pin's sampled 0/1 level current without activating the package when a separate hardware gate owns execution. This is still real trace delivery; only the chip's internal reaction is suppressed.
- RP2C02/RP2C07 CPU-side RS0-RS2, R/W and D0-D7 pins are passively sampled while `/CS` is the actual register-transaction activation gate. Ordinary CPU bus traffic therefore no longer wakes PPU register logic while the PPU is deselected.
- Mapper-0/NROM CPU and PPU sides are separated. CPU data pins are passive because mapper 0 has no write register; PPU AD0-D7 is sampled under ALE or /WR instead of activating cartridge logic on CIRAM data traffic; M2 work is rising-edge-only.
- RP2C02/RP2C07 retain the last externally presented AD, A8-A13, ALE, /RD and /WR states and drive package pins only when the physical output actually changes.
- HM6116 uses known-byte fast paths and skips address/data-only work when CS/OE/WE prove neither a read nor write can occur.
- SN74LS373 uses known-byte capture/output fast paths, retains its presented Q state, and ignores D-bus changes while LE is Low.
- RP2A03/RP2A07 no longer evaluate the nonlinear floating-point APU mixer every CPU cycle. A shared precomputed DAC table preserves the same mixer transfer function, and mixing/output publication occurs only when a channel output changes.
- Package output-change and receiver fan-out storage is reused by active count instead of clearing reference arrays after every reaction.

No signal queue, component scheduler, runtime settle pass, semantic motherboard shortcut, or host-side emulation bypass is introduced. The optimization rule is: **pins always receive their physical level; internal chip work runs only when that level can physically matter.**


## v1.3.4 — harmonic master-clock activation skipping

Release execution no longer iterates every master-clock cycle when the compiled oscillator trace proves that every receiver is rising-edge-only and none of their chip-owned divider periods can activate. Inactive low-to-low clock cycles are advanced arithmetically at the oscillator and clock pins, preserving exact half-cycle counts, rising-edge counts, and divider phases. The next real activation edge is still presented synchronously through the physical clock trace, and the receiving package reacts normally.

On the Famicom/NTSC clock trace this means the runtime jumps directly between RP2C02 /4 and RP2A03 /6 activation boundaries instead of executing software dispatch for every one of the ~21.5 million master cycles per emulated second. Profiling and single-half-cycle stepping retain the scalar physical path. No signal queue, motherboard scheduler, polling loop, or settle phase is added.

## v1.3.3 — rising-edge clock pin fast path

The topology-validated master-clock trace now has a dedicated package-pin path for rising-edge-only clock inputs. A High-to-Low edge is still physically delivered and retained as Low at RP2A03/RP2C02 (and PAL RP2A07/RP2C07) clock pins, but it returns at the pin boundary: no package logic, activation counter, divider work, or receiver dispatch is performed.

Low-to-High edges remain chip-owned. RP2A03 wakes only at its /6 activation boundary, RP2C02 at /4, RP2A07 at /8, and RP2C07 on each rising package-clock edge. Generic non-clock traces continue to use the normal input activation path. No signal queue, scheduler, polling, or runtime settle processing is introduced.

## v1.3.2 — compiled clock and coalesced direct fan-out

This release keeps the queue-free motherboard model and targets the measured v1.3.1 performance regression by removing repeated work from the highest-frequency physical paths rather than restoring a scheduler.

- One package output reaction is propagated as one synchronous change-set. All affected traces are presented first, destination input masks are coalesced, and each receiving package reacts once. The temporary fan-out frame is thread-local/reused call-frame storage, not a runtime signal queue.
- Repeated writes to the same physical trace during one package reaction are suppressed in O(1) using a package-local publication sequence instead of scanning the changed-net list.
- The master oscillator trace is topology-validated once as a single-driver connection. Release execution then toggles the oscillator driver and directly presents its 0/1 level without invoking the generic net resolver on every master-clock edge.
- Clock edge sensitivity/division is owned by each chip input pin. RP2A03 wakes only every sixth master-clock rising edge (one M2 half-cycle), RP2A07 every eighth, RP2C02 every fourth (one PPU dot), while the physical CLK pin still receives every High/Low level. RP2C07 remains every rising edge for its PAL package clock.
- Binary digital bus drive/release uses a prevalidated output-capable fast path and avoids repeated direction/contention checks per bit.
- Video/audio retained output capture uses a reusable ring buffer and bulk drain spans instead of one Queue<T> enqueue/dequeue plus delegate callback per sample. The desktop presenter also precomputes the eight NES emphasis palettes.
- NMI output is driven only when its asserted state changes; the RP2C0x extension bus is no longer redundantly released on every PPU output refresh.
- Profiling remains available. When profiling is enabled the generic propagation path is intentionally retained so diagnostic counters remain meaningful; normal Release execution uses the compiled clock path.

There is still no motherboard signal queue, component scheduler, runtime settle pass, or semantic CPU/PPU/memory shortcut. The normal model remains `chip output change-set -> physical traces -> coalesced destination input change-set -> chip reaction`.


## v1.3.1 — atomic package output publication

This release keeps the v1.3.0 queue-free motherboard model and fixes the ordering regression exposed by the full test suite. A chip still reacts directly to changed input pins and the board still propagates signals synchronously; however, all output pins changed by one chip reaction are now presented as one package-level change-set before any receiving chip executes.

- There is still no central signal, net, or component queue and no runtime settle loop.
- A package may change several output pins while processing one incoming transition. Those driver states are retained immediately inside the package, then all affected traces are resolved/presented before downstream packages react.
- Receiving packages accumulate only their changed input-bit mask for that direct change-set and execute immediately once the complete source-package output state is visible.
- Re-entrant input changes to a package already executing are retained locally on that package and consumed before it returns; the motherboard does not schedule or queue the package.
- Initial topology publication is two-phase: every trace first presents its static electrical level, then packages react. This prevents board construction order from inventing startup/reset edges.
- Tests that previously relied on multiple independent source changes being delayed until `Settle()` now use physically correct signal order. `Settle()` remains a no-op compatibility method and does not make separate source changes simultaneous.

The runtime path remains conceptually: `chip output change-set -> traces -> input pins -> receiving chip reaction`.


## v1.3.0 — immediate motherboard trace propagation

This release replaces the remaining central signal/net/component queue with the simple physical-board model: an output-pin change immediately changes its attached motherboard trace, the trace resolves any real shared-driver condition, and the resulting digital level is presented directly to connected input pins. The receiving package then reacts in its own world.

- Normal digital propagation is synchronous and queue-free: `output pin -> trace/net -> input pin -> receiving package`.
- `DigitalPin.Drive()` now starts the physical consequence immediately; it does not mark a signal for later scheduler work.
- `DigitalNet` contains only connection topology, driver resolution and compiled input routes. It no longer has scheduler indexes, dirty-net queue membership, or clock-source scheduler shortcuts.
- Receiving packages are invoked directly from their changed input pins/routes. There is no component activation queue and no changed-mask coalescing wait.
- The clock plan simply toggles the oscillator output. The clock trace immediately presents that 0/1 level to every connected package.
- Runtime motherboard classes no longer call `Simulator.Settle()` after power, reset, clock, controller or other source changes.
- `VirtualHardwareSimulator` is reduced to topology compilation and optional diagnostics/profiling. Its retained `Settle()` method is a no-op compatibility point for older tests/callers and performs no electrical work.
- A same-net re-entrant change is handled by electrical generation ordering: a newer resolved level supersedes an older level already being delivered, without a queue.
- Board-level logic remains explicit physical hardware. AND/NOR/inverter/latch/decoder/passive packages still react through their own pins; the motherboard never interprets CPU, PPU, RAM, ROM or cartridge semantics.

The common case is therefore deliberately small: a changed output produces a changed trace level and a changed input. Complexity exists only inside the actual chip or board component that physically owns it.


## v1.2.0 — chip-local hot-path cleanup

This release targets the measured ~11 FPS runtime without changing the physical chip boundary. It removes work that was still being performed after an input transition even when that particular pin could not affect the current chip operation.

- Package input-change masks are now assigned by the package when each pin is created. The simulator no longer manufactures package pin masks during topology compilation.
- RP2A03 and RP2A07 react immediately to NMI/controller input changes, but master-clock events no longer resample those unrelated inputs on every master edge. CPU/APU work still occurs only from the real master-clock input and its internal divider.
- RP2C02 and RP2C07 separate asynchronous CPU-register input activity from raster-clock activity. Falling master edges and non-dot RP2C02 divider phases no longer rerun VRAM output and NMI work.
- External VRAM/data changes remain retained on the package input pins and are consumed by the chip at the appropriate internal clock phase; they no longer cause unrelated PPU output recomputation.
- The simulator now executes concrete pin-reactive package objects rather than dispatching through the component interface on every activation.
- The redundant `_netQueued` bitmap and circular net queue bookkeeping are removed. `DigitalNet.IsDirty` is the single queue-membership state, and each dirty-net wave is resolved as one compiled batch.
- The defensive zero-mask check inside every package activation is removed because the electrical router never schedules a zero input mask.

The runtime rule remains unchanged: chips know only their own pins and internal state; every cross-package signal still travels through physical virtual pins and resolved nets.


## v1.1.6 — correct unchanged-bus boundary regression test

- Corrects the v1.1.5 boundary regression test so the external driver and package driver agree on `Low` while the package owns the bidirectional pin.
- Releasing the package driver therefore leaves the resolved bus at the same external `Low` level and must not create a new input activation.
- Does not change `DigitalPin`, net resolution, chip execution, CPU, PPU, or any runtime behavior.
- Preserves the strict rule that opposing strong drivers resolve to `Contention`.

## v1.1.5 — separate physical pin state from accepted input state

- Retains the v1.1.4 RP2C02 internal PPU-bus ownership fix; the v1.1.4 log shows the earlier integrated PPUDATA/render-fetch failure is no longer present.
- Bidirectional pins now retain two distinct concepts: the physical resolved level visible at the pin and the last level accepted while the pin was actually an input.
- A chip can therefore observe its own driven pin level without that output re-entering the chip as an input event.
- Releasing an unchanged externally driven bus does not manufacture an input transition.
- The v1.1.4 6502 analyzer read-settling correction is retained because it fixed late responder data visibility without changing chip execution.
- No polling, global chip scheduler, lifecycle callback, or peer-chip shortcut is restored.

## v1.1.4 — strict bidirectional input state and RP2C02 bus ownership

- A bidirectional pin that is actively driving no longer overwrites its retained input sample with its own resolved output. Releasing an unchanged external bus therefore cannot manufacture a false chip activation.
- The passive 6502 analyzer no longer freezes read data at PHI2 rising edge; responder data may settle later during the same high phase.
- RP2C02 external PPU-bus ownership is now internal to the chip: CPU PPUDATA access can temporarily own the PPU address/data/strobe pins without being consumed as a renderer memory response.
- An interrupted render fetch is retained and re-issued through the same PPU pins after CPU PPUDATA releases the bus.
- No polling, global chip scheduler, peer-chip reference, or direct CPU/PPU/memory shortcut is introduced.

## v1.1.3 — bidirectional release input delivery

- Fixes the electrical event boundary when a bidirectional package pin releases a shared bus.
- A package still cannot react to its own actively driven output.
- If releasing the pin exposes a different level driven by another package, that resolved level is now delivered as a genuine incoming input transition.
- Adds a regression test for bus hand-off from a local bidirectional driver to an external driver.
- Does not restore polling, scheduler-owned chip work, lifecycle callbacks, or chip-to-chip shortcuts.

## v1.1.1 — duplicate input-handler compile fix

- Removes the accidental duplicate `MaskProbe.OnInputChanges(ulong)` override from `VirtualHardwareFoundationTests`.
- Does not restore any removed scheduler, polling, lifecycle, or compatibility API.
- Keeps the v1.1.0 pin-reactive chip boundary unchanged.

## v1.1.0 — pin-reactive chip packages

This release makes the virtual chip boundary explicit and removes the remaining package lifecycle and callback side channels.

A virtual chip package now has one runtime entry point: the electrical kernel reports which of that package's input-capable pins changed. The package may inspect only its own pin levels and retained internal state, then drive or release only its own output-capable pins.

Removed from the chip contract:

- polling `Evaluate()` calls;
- package `PowerOn()` and `Reset()` calls from boards or the simulator;
- startup evaluation of every package;
- scheduler-owned chip work contracts;
- board or simulator references inside chip packages;
- chip-to-host pixel, audio, and trace callbacks;
- output-only and self-driven bidirectional pin wake-ups.

Power, reset, clock, enable, address, data, controller, DMA, cartridge, CPU, PPU, and APU state changes now enter packages through connected input pins. Motherboard power-on establishes only external source drives. Video, audio, and trace leave their packages through retained output-sample pins that the host drains after an execution batch.

The cartridge image loader and diagnostic inspection helpers remain configuration/inspection surfaces; they do not connect one runtime chip directly to another or bypass electrical execution.

## Current execution model

- **Chips** know only their own pins and internal state.
- **Input pins** are the only runtime cause of chip execution.
- **Output pins** are the only runtime effect a chip can have on the surrounding circuit.
- **Motherboards** own composition and wiring, never chip behavior.
- **External sources** establish rails, reset, clocks, switches, and host-controlled physical inputs through their own output pins.
- **The simulator** compiles static topology and optionally records diagnostics; it is not in the normal signal-delivery path.
- **The desktop host** drains video/audio output samples; it does not receive callbacks from chips and does not emulate them.

There is no alternate emulator execution path and no CPU-to-memory, PPU-to-renderer, mapper, controller, DMA, or audio shortcut in the VirtualHardware runtime.

## Included motherboard compositions

- Japanese Famicom
- NTSC NES
- PAL NES

## Build and test

From the repository root:

```powershell
dotnet test
```

## Run a ROM

```powershell
dotnet run -c Release --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost -- "C:\ROMs\Game.nes" --board famicom
```

Available board selectors: `famicom`, `ntsc`, `pal-a`, `pal-b`, and `auto`.

Enable opt-in simulator profiling with:

```powershell
dotnet run -c Release --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost -- "C:\ROMs\Game.nes" --board famicom --profile
```

## Hardware rules

1. A chip may retain only its own state and references to its own pins or internal subcircuits.
2. A chip reacts only when one or more declared input-capable pins change level.
3. A chip affects the machine only by driving or releasing its own output-capable pins.
4. A chip may not retain motherboard, simulator, host, renderer, audio sink, memory-device, or peer-chip references.
5. Motherboards own composition and wiring, never replacement chip behavior.
6. Static topology may be compiled once, but every runtime signal still travels through installed virtual pins, nets, buses, and packages.
7. Runtime digital behavior propagates immediately through physical traces; there is no central signal, net, or component queue.
8. Motherboards never suppress a connected input because a chip is deselected. Pins always receive the electrical level; power/select/enable/edge rejection is owned by the receiving chip.

## Repository layout

```text
src/Products/NES/
  AxetosOS.Products.NES.VirtualHardware/   chips, boards, wiring and simulator
  AxetosOS.Products.NES.DesktopHost/       native video/audio/input host
  AxetosOS.Products.NES.HeadlessHost/      diagnostic host
  AxetosOS.Products.NES.Abstractions/      shared contracts
  AxetosOS.Products.NES.Cartridges/        cartridge metadata and loading
  AxetosOS.Products.NES.Hardware/          established reference implementation

tests/AxetosOS.Products.NES.Tests/         hardware and regression tests
```
