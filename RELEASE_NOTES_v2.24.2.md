# AxetosOS Products / NES v2.24.2 — M2 read-phase integration hotfix

v2.24.2 addresses the 11 integration failures reported after v2.24.1 compiled successfully and discovered all 276 tests.

## Root cause

v2.24.0 correctly rebuilt the motherboard CPU decoder so RAM, PPU registers and cartridge `/ROMSEL` are qualified by M2. The RP2A03/RP2A07 bus core still sampled external D0-D7 when the following M2-high CPU boundary executed the microcycle. With the corrected decode, the selected external device had already released the bus at M2 falling. The physical runtime could therefore consume stale retained data while compiled execution resolved the intended bus byte directly.

## Hardware fix

- RP2A03 and RP2A07 now capture external read data while the active M2-high window is still present, immediately before M2 falls.
- The captured byte is retained inside the CPU package and consumed by the following microcycle.
- Compiled RP2A03 resolves complete-phase reads at the same M2-falling boundary and does not re-read the target on the following M2-high phase.
- External open-bus retention remains package-local when no device drives D0-D7.
- `$4015`, `$4016` and `$4017` remain internal CPU/APU/controller read paths and are not converted into external-bus captures.

This phase correction is expected to restore physical/compiled agreement for NROM execution, APU-register programs and MMC1 serial/RMW/CHR timing without reverting the corrected v2.24.0 LS139 `/ROMSEL` topology.

## Test corrections

- MMC1 connector names are component-qualified (`TEST.MMC1.CPU.A14`, etc.); the test now checks the physical suffix rather than an impossible unqualified global name.
- Controller `$4016` readback retains internal open-bus D5-D7; the existing fixture therefore stores `$41`, not `$01`, after the preceding `$40` operand-high read.
- RP2C02/RP2C07 expose `PreparedSpriteCount` for the completed dot-257 sprite set while retaining `EvaluatedSpriteCount` for the currently-running evaluation. The two visible-sprite tests now assert the prepared count after advancing into the next scanline.

## Validation

Expected discovered test total remains **276**. This environment cannot execute the .NET suite, so local `dotnet test` remains the acceptance gate. Do not Git-push until the suite passes.
