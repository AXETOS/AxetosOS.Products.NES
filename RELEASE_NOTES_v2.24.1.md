# AxetosOS Products / NES v2.24.1 — compile hotfix

v2.24.1 fixes a single compile error in the new v2.24.0 Famicom motherboard decoder regression test.

## Fix

`VirtualHardwareFamicomMotherboardTests` incorrectly referenced `board.Ground.Rail.Net`. `Ground` is a `DigitalPowerRail`, whose package pin is exposed as `Output`, so the assertion now correctly uses `board.Ground.Output.Net`.

## Runtime behavior

No production hardware implementation changes from v2.24.0. CPU, PPU, APU, motherboard, cartridge, NROM, MMC1, compiler and runtime source are byte-for-byte unchanged by this hotfix.

## Validation

The expected discovered test total remains **276**. Local `dotnet test` is the acceptance gate.
