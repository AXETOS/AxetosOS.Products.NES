# AxetosOS Products / NES v2.27.0

## Bound-topology dispatch performance sweep

The validated v2.26.1 Alien Syndrome normal-runtime baseline is 45.77 FPS. v2.27.0 concentrates on generic compiler overhead that still occurs around the physical replaceable-cartridge boundary; no post-patch FPS is claimed until the Release/uncapped benchmark is run locally.

### Changes

- Adds `ICompiledStaticCombinationalComponent`, an opt-in hardware facet for package outputs that are guaranteed independent of mutable internal state while the device remains attached.
- NROM exposes fixed CIRAM enable/mirroring outputs through that facet.
- MMC1 exposes only state-independent `/CIRAM-CE` and IRQ behavior. Its mapper-controlled `CIRAM A10` remains runtime/live and is never frozen by the bind-time proof.
- The generic compiled bus classifies dynamic target conditions per address when a cartridge is bound. Proven-rejected targets no longer enter runtime dynamic resolution for that address; proven-selected conditions skip repeated recursive topology evaluation.
- Fixed address projections are precomputed into per-address local bases. Runtime evaluates only address bits that remain physically unresolved; MMC1 CIRAM therefore keeps its live mirroring bit while avoiding reconstruction of fixed address wiring on every nametable access.
- Empty begin-read phases are skipped, and a one-static-target/no-dynamic-target read can return directly without constructing contention state.
- Compiled target read/write/observer delegates and single bus-cycle observers are cached on the runtime object.
- IRQ sampling uses a topology-bound driver sampler rather than rediscovering output-capable pins on every sample.
- RP2A03 calls compiled end-of-cycle write completion only after a compiled write, not after every read cycle.
- Famicom ejection removes the package from the physical netlist before the compiled binding is rebuilt, so bind-time proofs/signal samplers always reflect the post-ejection topology.

### Architecture preserved

The motherboard remains electrically/topologically defined and has no mapper semantics. The cartridge remains a separate replaceable runtime unit. All mutable MMC1 register behavior remains cartridge-owned. The new bind-time tables are compiler dispatch caches derived from physical pins, topology, and explicit package facets; replacing the cartridge rebuilds them.

### Verification

One regression is added to prove MMC1 `/CIRAM-CE` may be statically classified while MMC1 `CIRAM A10` may not. The expected suite total is 280 tests based on the validated v2.26.1 total of 279. This environment has no .NET SDK, so local `dotnet test -c Release` is required before commit, followed by uncapped Alien Syndrome and `--compiled-lab` NROM benchmarks.
