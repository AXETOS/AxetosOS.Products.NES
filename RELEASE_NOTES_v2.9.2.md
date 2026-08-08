# AxetosOS Products / NES v2.9.2

## Compiled package fan-out receiver identity correction

- Baseline: v2.9.1 clean compiled package-fanout experiment.
- Fixes the remaining compiled/reference equivalence failure at the cartridge CPU-read counter.
- Root cause: a physical input route may be compiled during connector insertion before the simulator assigns the newly inserted package its final component index. The legacy propagation frame executed the retained package reference, while v2.9.0/v2.9.1 incorrectly used the cached index as runtime identity.
- The compiled fan-out now accumulates the actual receiving `VirtualHardwareComponent` reference for execution and retains the cached index only for optional profiling attribution.
- Physical ordering from v2.9.1 remains unchanged: input pins accept and stage immediately; destination package execution is deferred until the source package's complete atomic output set has been presented.
- The old propagation-frame hot path remains disabled for multi-output package fan-out. No compatibility fallback is restored.
- No NES/Famicom/NROM/component-type semantics are introduced.

Expected tests: 228 total. Run `dotnet test` locally before benchmarking.
