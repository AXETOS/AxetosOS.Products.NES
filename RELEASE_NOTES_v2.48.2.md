# AxetosOS Products / NES v2.48.2

## VRC6 chip-local audio hot-path optimization

Akumajou Densetsu commercially exercised the VRC6 package at sustained scale and exposed avoidable work in the chip-local expansion-audio path.

- Preserve one physical VRC6 audio clock for every CPU cycle. No channel clocks, timer decrements, phase transitions, accumulator steps, register effects, DAC states, or diagnostic counters are skipped.
- Stop reevaluating stable pulse-channel combinational output on countdown cycles where no timer phase can change.
- Stop reevaluating the saw DAC on sequencer steps that cannot change its accumulator.
- Recompute the retained mixed cartridge DAC only when at least one channel output node actually changes, while still clocking all three channels every CPU cycle.
- Mark the resulting per-cycle chip-local operations for aggressive inlining.
- No motherboard, compiler, mapper-banking, IRQ, CIRAM/CHR routing, host-audio, or generic analog-boundary semantics are changed.

The validated incoming baseline is 656 tests. This release does not add or remove tests; the same 656-test suite must remain green before runtime performance is evaluated.
