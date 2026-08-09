# AxetosOS Products / NES v2.26.1

## Patch-layout hotfix

v2.26.1 corrects the archive layout of v2.26.0. The previous patch stored changed files under `AxetosOS/src/...`, while the source repository is rooted at `AxetosOS/AxetosOS.Products.NES/...`. As a result, the files could be extracted without replacing the active project sources.

The local verification run exposed this directly: Alien Syndrome still printed the pre-v2.26 runtime banner (`physical virtual-hardware buses and master clock only`) and remained on the raw path at 19.40 FPS.

This archive republishes the complete intended v2.26 implementation at the correct repository paths:

- normal Famicom startup preserves the specialized compiled NROM runtime when available;
- otherwise normal startup enables the product-agnostic whole-circuit compiler before power-on, including MMC1;
- `--raw-hardware` selects the uncompiled diagnostic path;
- `--reference-runtime` remains an alias;
- the generic compiler retains the v2.26 flattened static-dispatch optimization;
- cartridge mapper/ROM hardware remains a separate replaceable external runtime unit.

No 60 FPS claim is made for MMC1 until measured locally in Release/uncapped mode.
