# AxetosOS Products / NES v2.46.3

## Generic compiled-bus cycle-completion phase

- Fixes the Mapper 69 raw/compiled parity failure where `CpuCycleClockCount` was one cycle ahead in compiled execution at an arbitrary master-clock stop boundary.
- Adds a product-agnostic `CompiledBusCycleObservationPhase` facet so package circuitry can declare whether it observes the opening or closing edge of every physical bus cycle.
- Adds a completion-phase observer path to the generic compiled bus fabric without adding mapper, CPU, PPU, NES, or board semantics to the compiler.
- RP2A03 compiled execution now publishes the physical M2 falling/completion edge after read data capture and before end-of-cycle write latches. The hot path only crosses this observer boundary when compiled hardware actually requests completion-phase observation.
- Sunsoft FME-7/5B binds its CPU-cycle IRQ counter and PSG clock to that completion edge, matching the raw package path where the circuitry reacts to M2 falling.
- Keeps `$8000/$A000` mapper and `$C000/$E000` PSG writes latched after the cycle clock, preserving the raw hardware ordering.
- Extends Mapper 69 conformance to assert the declared completion-phase facet.

This is a generic physical-timing correction. It does not add NES-specific behavior to the motherboard or compiler.
