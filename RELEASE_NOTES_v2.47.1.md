# AxetosOS Products / NES v2.47.1

## Konami VRC IRQ cycle-mode prescaler hotfix

- Fixes the reusable `KonamiVrcIrqCounter` so cycle mode clocks the IRQ counter directly without also advancing the scanline-mode 341-dot prescaler.
- Keeps the prescaler initialized and stable while cycle mode is active; the 341/−3 divider remains exclusive to scanline mode.
- Strengthens the VRC4 cycle-mode conformance test to assert the prescaler stays at 341 after CPU-cycle clocks.
- No motherboard, compiler, PRG/CHR banking, mirroring or mapper address-decode behavior is changed.

Validation must be performed with `dotnet test -c Release` on the user machine.
