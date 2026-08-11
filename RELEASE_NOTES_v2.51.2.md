# AxetosOS Products / NES v2.51.2

## MMC5 retained CHR A/B fetch-phase selection

- Corrects MMC5 CHR A/B selection to be retained fetch-phase state rather than a combinational function of the live tile counter.
- Latches CHR-set selection on MMC5-observed nametable fetches before scanline detection can reset the tile counter, matching the package-visible fetch ordering needed by 8x16 sprite software.
- Refreshes the retained selection on CHR bank/mode writes, PPU control snoops, PPU-idle frame exit and NMI-vector frame reset.
- Adds CHR-A, CHR-B and CHR-set-switch diagnostics for commercial validation.
- Adds conformance coverage for the scanline-reset boundary that previously switched banks too early, and extends raw-vs-compiled parity to the retained CHR state/counters.
- No motherboard or mapper-specific compiler semantics were added.
- Expected Release suite: 760 tests.
