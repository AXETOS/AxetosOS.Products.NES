# AxetosOS Products / NES v2.9.0

## Experimental compiled package fan-out replacement

- Baseline: validated v2.6.0 shared-chip core.
- Removes/disables the v2.8 optional generic topology runtime for a clean benchmark.
- Replaces the steady-state package-output propagation-frame path with one startup-compiled anonymous fan-out transport per physical package.
- Every physical net still resolves independently with the existing electrical driver-strength, Hi-Z, unknown, and contention rules.
- Every connected physical package pin still receives the resolved net level before any destination package reacts.
- Destination input masks are accumulated directly by component index and reserved for all affected packages before reactions begin, preserving package-boundary atomicity under nested propagation.
- Chips retain no board, peer-package, wire, callback, or product knowledge.
- The fused Famicom/NROM runtime remains unchanged and available as the proven fast fallback.

Expected tests: 228 total. This environment has no .NET toolchain, so run `dotnet test` locally before benchmarking.
