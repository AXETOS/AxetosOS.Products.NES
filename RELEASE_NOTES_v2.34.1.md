# AxetosOS Products / NES v2.34.1

Validation hotfix for the v2.34.0 Mapper 11 / Color Dreams release.

## Changes

- Corrected the Color Dreams generic-compiled-vs-raw parity test fixture to select the Famicom region explicitly through its Japan source name, matching the Famicom-only compiled-lab execution path used by the comparison. The v2.34.0 fixture used a USA source name, selected the NTSC NES motherboard, and therefore failed before exercising the compiled mapper path.
- Hardened opt-in profiling section accounting: a profiled section that begins and ends within the same `Stopwatch` tick now still counts as a valid sample with zero measured ticks. This removes host-timer-resolution flakiness without fabricating elapsed time or weakening the profiler assertions.
- Mapper 11 hardware behavior, motherboard semantics and generic compiler semantics are unchanged.

## Validation

The expected Release suite remains **359 tests**. Local .NET validation is required before this release is considered validated.
