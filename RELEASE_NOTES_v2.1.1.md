# AxetosOS Products / NES v2.1.1

## Real-time pacing and fused APU conformance

v2.1.1 retains the v2.1.0 fused physical Famicom/NROM execution architecture and addresses two observations from the first real-time-speed game tests: the compiled machine could run faster than the physical console, and the existing compiled/reference equivalence test did not cover APU output.

### Changes

- DesktopHost now synchronizes normal execution to the physical 21,477,272 Hz Famicom/NTSC master clock.
- Added `--uncapped` for raw performance/headroom tests.
- `--profile` remains uncapped so profiling measures execution cost rather than pacing sleep.
- Added final audio-core diagnostics: APU CPU cycles, DAC output event count, and current DAC level.
- Added a synthetic NROM APU conformance test that writes RP2A03 pulse-channel registers and requires compiled/reference DAC sample equality and non-zero output.

### Validation target

Expected test count after applying this patch: **225 tests**.

The .NET SDK is not available in the patch-generation environment, so the user must run `dotnet test` locally before this release is considered validated.
