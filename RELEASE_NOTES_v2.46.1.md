# AxetosOS Products / NES v2.46.1

Mapper 69 / Sunsoft 5B compile hotfix.

- Resolves three `Math.Max` overload ambiguities in `Sunsoft5bPsg` under the current .NET 8 compiler by normalizing the `ushort`/`byte` period values to `int` before comparison.
- No mapper, PSG, IRQ, timing, banking, mirroring, or cartridge-boundary behavior changes.
- Expected Release suite remains 596 tests after v2.46.0 plus this hotfix.
