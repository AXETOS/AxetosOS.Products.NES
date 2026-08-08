# AxetosOS Products / NES v2.7.0

## RP2A0x packed CPU/package hot-path sweep

This release is an aggressive shared-core performance experiment built on the validated v2.6.0 baseline. It changes only RP2A03/RP2A07 internals and release metadata.

### Changes

- Byte-sized retained CPU state enums.
- 256-entry chip-local opcode plan table covering 193 fixed operation/addressing-mode combinations.
- Small hot decoder separated from the cold special-opcode switch.
- Retained package output state for A0-A15, D0-D7, R/W and controller /OE pins, avoiding repeated scans when this chip's output has not physically changed.
- RP2A03 steady-state master-clock path aligned with the already optimized RP2A07 package path.
- Bit-mask operation classification for stores and read-modify-write cycles.
- Packed direct ALU status updates for common shift/rotate/ADC/compare/BIT operations.
- Packed APU channel output-change state before DAC mixing.
- Fast reject for writes outside the RP2A0x integrated $4000-$4017 register window.

### Hardware boundary

The RP2A0x still knows only its own package pins and internal CPU/APU/DMA/controller circuits. It has no motherboard, RAM, PPU, cartridge, mapper or wire knowledge. Every real external output transition still leaves through the normal physical package-pin/electrical-net mechanism.

### Validation

The rewritten Z/N and shift flag logic was exhaustively compared against the prior implementation for all byte/status combinations. ADC status behavior was exhaustively compared across all accumulator/value/carry combinations. The opcode plan table was generated from and checked against the existing decoder mapping.

The .NET toolchain is not available in the patch-generation environment. Expected local suite: **228 tests**. Run `dotnet test` before benchmarking or committing.
