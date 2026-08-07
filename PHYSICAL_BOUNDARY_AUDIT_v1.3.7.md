# v1.3.7 Physical IC Boundary Audit

This audit applies one rule to the NES virtual-hardware runtime: a physical IC is one motherboard-visible package. Functional blocks physically inside that IC communicate directly through package-local state/classes. Only actual external package pins are connected to motherboard traces.

## Electrical boundary

- `VirtualHardwareComponent` retains its own `DigitalPin` objects and internal state only. It no longer stages or retains `DigitalNet` references while executing a chip reaction.
- Changed chip outputs are staged as owned package pins. After the chip finishes its internal reaction, `DigitalPin` crosses the physical package boundary and publishes those final drive states to attached traces.
- `DigitalNet` is topology/transport only. It resolves drivers, presents the resulting level to connected pins, and invokes packages whose pins report an activating transition. It does not inspect/cache rising-edge, falling-edge, divider, CPU, PPU, RAM, DMA, APU, mapper, select, or enable semantics.
- `DigitalPin` owns edge/divider activation state. Higher-level power/select/enable behavior remains in each chip's own input handler.

## Large Ricoh ICs

- `Rp2A03`: CPU execution, DMA, APU frame logic, pulse/triangle/noise/DMC blocks and internal counters/state remain inside one `Rp2A03` package. APU channel classes are private package-local classes, not motherboard components.
- `Rp2A07`: same physical boundary rule for the PAL CPU/APU package.
- `Rp2C02`: timing, register interface, VRAM bus sequencing, background pipeline, sprite evaluation/render state, palette/state counters and pixel generation remain inside one `Rp2C02` package.
- `Rp2C07`: same physical boundary rule for the PAL PPU package.
- CIC3193/3195/3197 remain single package components with package-local state.

## Discrete ICs/components

The physical board continues to expose discrete packages such as HM6116 SRAM, SN74LS139A decoder, SN74LS368A buffer, SN74LS373 latch, controllers, power/reset parts and actual cartridge-board connections as motherboard components. Their own internal state does not create private motherboard nets or peer-package graphs.

## Removed artificial boundaries

The following synthetic execution pieces were removed because they represented functions inside a real CPU/PPU as separate motherboard-visible components: `NesCpuMotherboard`, `NesPpuTimingCore`, `NesPpuRegisterPackage`, `NesPpuMemoryDevice`, `NesOamDmaController`, `NesControllerIoPackage`, `Rp2C02BusSequencer`, `Rp2C02DataBufferRegister`, and `Rp2C02VramAddressRegisters`.

The older high-level `AxetosOS.Products.NES.Hardware` execution project and its `HeadlessHost` were also removed so the repository no longer carries a second NES CPU/PPU execution architecture beside `VirtualHardware`. ROM/cartridge file metadata utilities remain separate because they are loading concerns rather than virtual motherboard devices.

## Structural enforcement

`VirtualHardwarePhysicalBoundaryTests` reflects over concrete virtual-hardware components and rejects component fields that contain peer components, private boards, or private nets, including arrays/generic containers. Separate tests verify the component base itself stores owned pins rather than motherboard nets and that the physical regional boards expose the real RP2A03/RP2A07 and RP2C02/RP2C07 package boundaries.

This is a source/architecture audit. Runtime correctness and performance still require the local `dotnet test` and ROM benchmarks after applying the patch.
