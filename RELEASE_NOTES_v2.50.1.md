# AxetosOS Products / NES v2.50.1

## VRC7 compile hotfix

- Resolves CS0121 in `KonamiVrc7Audio` by explicitly promoting the 9-bit `FNumber` (`ushort`) to `int` for the existing `Math.Max` phase-step expression.
- No VRC7 banking, IRQ, SRAM, register decode, FM timing, envelope, operator, DAC, motherboard or compiler behavior is changed.
