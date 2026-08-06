# AxetosOS Products / NES

A modular AxetosOS hardware product that builds NES-family machines from reusable, pin-driven virtual hardware modules.

## Current status

**VirtualHardware version: v0.86.5**

The Famicom composition boots and runs real NROM software through the virtual RP2A03, RP2C02, RAM, address latch, controller hardware, cartridge board, pins, nets and clocks. Video comes from the RP2C02 output and audio comes from the RP2A03 DAC output.

I am actively improving performance with the goal of reaching full real-time execution without compromising the modular, pin-driven hardware design. Donkey Kong on the Famicom board is currently used as the main performance and correctness benchmark.

## Architecture

AxetosOS products are assembled from reusable modules. In this product:

- **chips** are independent modules that operate through physical virtual pins;
- **motherboards** are product composition roots that select chips and define wiring, clocks, power, reset, slots and outputs;
- **cartridges** are plug-in hardware compositions;
- **the simulator** provides generic electrical resolution, scheduling and topology compilation;
- **the running NES** emerges from the assembled motherboard, chips and cartridge.

The shared engine does not contain direct CPU-memory, PPU-tile, mapper, renderer or APU shortcuts. Motherboard-specific execution plans may use known static board flow, but they must still execute the installed chip modules and resolve the real virtual wiring.

## Included motherboard compositions

- Japanese Famicom
- NTSC NES
- PAL NES

A different or enhanced motherboard can reuse the same chip modules, replace chips, add memory or peripherals, and compile a different execution plan from its own topology.

## Power-on motherboard compilation

At power-on, ROM load or after a topology change, the motherboard builds an in-memory execution plan from the installed chips, their pin/activation contracts and the actual wiring.

The plan records:

- pin roles and package ownership;
- chip activation and gating conditions;
- direct electrical routes;
- package-level changed-input masks;
- clock and sequential boundaries;
- the components that can actually react to each signal path.

This startup work is intentionally performed once. A visible loading pause is preferable to repeating topology and activation checks during millions of simulated hardware cycles.

The chips remain authoritative for their internal state and outputs. The motherboard only routes signals and decides when a real chip package can possibly need evaluation.

## v0.85.3 — direct motherboard signal-chain routing

- Keeps a single causal chip-and-net branch inside one compiled motherboard routing loop.
- Routes output nets immediately after the real chip drives its pins.
- Sends resolved input changes directly to the next activated package in the same chain.
- Avoids returning to the global net and component queues between every step when no unrelated hardware event is waiting.
- Preserves the global ordering path whenever multiple independent events are already pending.
- Continues to use the actual chip implementations, pins, electrical net resolution, activation contracts and package input masks.
- Rebuilds all direct routing structures when the motherboard topology changes.

The generic event kernel remains the fallback and correctness path. Direct routing is selected only when the motherboard can prove that the remaining work is one causal branch.

## Repository layout

```text
src/Products/NES/
  AxetosOS.Products.NES.VirtualHardware/   reusable chips, boards, wiring and simulator
  AxetosOS.Products.NES.DesktopHost/       native video/audio/input host
  AxetosOS.Products.NES.HeadlessHost/      diagnostic host
  AxetosOS.Products.NES.Abstractions/       shared contracts
  AxetosOS.Products.NES.Cartridges/         cartridge metadata and loading
  AxetosOS.Products.NES.Hardware/           established reference implementation

tests/AxetosOS.Products.NES.Tests/          hardware and regression tests
```

## Build and test

From the repository root:

```powershell
dotnet test
```

## Run a ROM through VirtualHardware

```powershell
dotnet run -c Release --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost -- "C:\ROMs\Game.nes" --board famicom
```

Available board selectors include `famicom`, `ntsc`, `pal-a`, `pal-b`, and `auto`.

The desktop title reports current, minimum, maximum and average FPS. Moving or resizing the window can temporarily distort the minimum measurement.

## Hardware-simulation rules

1. Chips remain independently testable and reusable.
2. Motherboards own composition and wiring, not replacement chip behavior.
3. ROM data changes dynamic chip behavior; it does not alter motherboard topology.
4. Optimized execution may precompile static board flow but may not bypass pins, nets, clocks, buses or installed chips.
5. Replacing a chip, cartridge or connection invalidates and rebuilds the compiled topology.
6. Generic execution remains the correctness fallback.

## Performance direction

The target is native real-time operation while preserving the hardware model. Work is focused on:

- motherboard-owned signal routing;
- chip-published activation contracts;
- package-level changed-input and output masks;
- topology-derived dependency routes;
- dense pin/net/component indexes;
- packed buses;
- explicit sequential boundaries;
- validation between generic and compiled execution.

The Famicom is the first demanding benchmark, not a special case inside the generic engine.

## License

MIT. See [LICENSE](LICENSE).


### v0.85.3 direct-routing safety

Direct motherboard signal chains now stop at stateful, clocked, timed, memory, CPU, PPU, and other sequential package boundaries. Only chips that explicitly declare zero-delay combinational behavior may remain inside one direct chain. This preserves the physical signal route while preventing a fast path from advancing hardware that must wait for a later clock or control event.

### v0.85.3 correctness rollback

The experimental direct causal signal-chain fast path is disabled for complete motherboard execution after Super Mario Bros. exposed an undefined internal-RAM read at `$07DC`. The validated strict indexed event ordering remains active while the direct-route compiler is redesigned with full bus-phase equivalence tests.


### v0.85.4 strict-routing regression test correction

The combinational propagation regression test now verifies that the disabled experimental direct-route counter remains unchanged while the complete signal chain still settles through the validated strict indexed event queue. This corrects the contradictory v0.85.3 assertion without weakening the electrical propagation checks.


### v0.85.5 SRAM power-up stabilization

HM6116 SRAM now powers up with an arbitrary but electrically determinate bit pattern. Real SRAM does not retain its previous contents after power loss, but powered cells settle to concrete zero or one values rather than remaining indefinitely unknown. This prevents valid software reads of untouched work RAM from producing an undefined CPU data bus. The experimental direct motherboard signal-chain fast path remains disabled.





### v0.86.5 RP2C02 CPU-bus transaction latching

The RP2C02 CPU register port now captures register select, direction and write data once on the active edge of `/CS`. The captured transaction remains authoritative until `/CS` is released, so electrical settlement or transient address/R/W changes cannot create duplicate `PPUSTATUS` reads or interrupt paired `PPUSCROLL` writes. Held reads keep their latched data on D0-D7 while register side effects occur exactly once.

### v0.86.4 RP2C02 split-screen timing trace

Adds an opt-in `--ppu-split-trace` desktop-host diagnostic that records sprite-zero hits and CPU accesses to PPUSTATUS, PPUSCROLL and PPUADDR with exact frame, scanline, dot and `v/t/x/w` state. This isolates Super Mario Bros. split-screen timing faults without changing emulation behaviour.

### v0.86.3 DesktopHost audio-interface build correction

- Corrected `NativePcmAudioSink` to implement the existing `IVirtualNesAudioSink` contract.
- This resolves the DesktopHost-only `CS0246` build failure that was not exercised by `dotnet test`.
- Release validation now requires building the DesktopHost explicitly in addition to running the test suite.

### v0.86.2 atomic completed-frame presentation

The native desktop video sink now uses separate render and completed-frame buffers. The PPU writes the active frame only into the render buffer, and the buffers swap after the final visible pixel. This prevents a large master-clock batch from partially overwriting the frame waiting to be presented with pixels from the next frame.

### v0.86.0 RP2C02 master-clock divider correction

The NTSC RP2C02 now advances its raster, external VRAM transaction engine, background pipeline and sprite pipeline at the physical master-clock divided-by-four rate. Its CPU register port remains asynchronous and is still observed on every package evaluation. This restores the required three PPU dots per CPU cycle relationship used by timing-sensitive games such as Super Mario Bros. for sprite-zero split scrolling.

### v0.85.6 deterministic cold-start RAM state

HM6116 SRAM now selects an all-zero, electrically defined state after power is applied. Physical SRAM power-up data is unspecified, and zero is one valid settled state. Using zero prevents an arbitrary deterministic pattern from accidentally matching a game's warm-reset signature. Super Mario Bros. exposed this by bypassing its normal title-screen initialization and entering gameplay with corrupted scroll state. The strict indexed event queue remains the active execution path.


## v0.86.2

- Emits each RP2C02 output pixel once even though the raster position remains stable for four console master-clock observations.
- Prevents repeated final-pixel notifications from swapping the desktop frame buffers multiple times and returning the presenter to a blank buffer.
- Keeps completed-frame presentation atomic while preserving the corrected master-clock divider.
