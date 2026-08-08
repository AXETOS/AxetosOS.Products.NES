# AxetosOS Products / NES v2.2.0

## True-hardware electrical hot-path sweep

- Restores direct/unrolled three-driver and four-driver `DigitalNet` resolution over the actual package pin drive states.
- Removes the incremental multi-driver shadow-state counters/bookkeeping from the normal electrical resolver.
- Preclassifies input-only and bidirectional `AnyChange` package pins at topology compilation time.
- Adds specialized pin acceptance paths that still store every delivered physical level and preserve chip-owned wake gating and bidirectional self-drive isolation.
- Retains exact strong-over-weak, Hi-Z, unknown and contention resolution semantics.
- Retains package-boundary atomic multi-output publication and same-trace final-state behavior.
- Adds four-driver contention/strength conformance and bidirectional fast-path conformance tests.

Expected test count after applying the patch: **226**.

This optimization is generic electrical infrastructure. It contains no NES/Famicom/CPU/PPU/mapper routing knowledge and does not teach any chip about another chip or about motherboard wiring.
