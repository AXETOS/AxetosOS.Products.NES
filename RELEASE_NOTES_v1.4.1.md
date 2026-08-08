# AxetosOS Products / NES v1.4.1

Profiler-guided compiled shared-bus electrical transport sweep.

The v1.4.0 profile showed that chip-owned pin gating reduced internal package wakeups but left the motherboard performing more than a billion trace resolutions and several billion physical pin deliveries per ~500-frame run. This release targets that remaining topology/electrical cost without moving chip semantics into the motherboard.

## Changes

- Three-or-more-driver traces use a topology-compiled strong/weak driver aggregate instead of rescanning the full driver array on every published change.
- Multi-output package publication updates all changed driver states before resolving shared traces, preserving the existing package-boundary atomicity.
- A shared trace touched by multiple outputs from one package reaction is resolved once from the complete final driver state.
- Chip-gated `AnyChange` pins retain every delivered level and accepted-input history while avoiding unnecessary old-level comparison work when wake is disabled.
- Output-only package-pin observation uses a topology-proven direct store path.
- NES-hot bus widths receive specialized 6/8/11/16-bit sampling and 8-bit strong drive/release paths.
- Adds regression coverage for compiled multi-driver strength/contention/release behavior and atomic same-trace multi-output package publication.

## Architecture

Unchanged and non-negotiable: motherboard traces know only physical topology, drive levels/strengths and electrical resolution. Every resolved level is delivered to connected package pins. `/CS`, `/OE`, `/WE`, clocks, edge semantics and other activation meaning remain owned by the chips. No signal queue, scheduler, settle engine or skipped physical clock pulse is introduced.

Run `dotnet test` before benchmarking. If it passes, compare normal Release Mario and Donkey Kong FPS against v1.4.0 (22.20 / 19.27 FPS) and then use `--profile` again if the gain is still insufficient.
