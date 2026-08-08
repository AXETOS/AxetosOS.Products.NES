# AxetosOS Products / NES v2.10.0

## Drastic packed electrical resolver experiment

This release starts from the validated v2.6 source and replaces the hot 2/3/4-driver `DigitalNet` arbitration logic rather than adding another runtime around it.

- Removes the v2.9 compiled package-fanout implementation from the compiled assembly when applied over a v2.9.x tree.
- Keeps the v2.8 generic transport disabled.
- Compiles every common 2/3/4-driver net into a 16-bit word containing four anonymous physical driver lanes.
- Updates only the changed driver's four-bit lane when a physical output changes.
- Resolves the complete shared net with one 64 KiB truth-table lookup.
- Preserves individual package-pin drive state, weak/strong priority, Hi-Z, Unknown and Contention.
- Leaves single-driver traces on the proven direct path and >4-driver laboratory traces on the generic resolver.
- Adds exhaustive four-driver electrical truth-table conformance coverage.

Expected suite: **229 tests**.

Performance is intentionally unclaimed until local same-machine A/B benchmarking against v2.6.
