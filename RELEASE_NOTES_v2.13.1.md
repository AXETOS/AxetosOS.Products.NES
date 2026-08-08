# AxetosOS Products / NES v2.13.1

## MMC1 physical write-strobe hotfix

This hotfix keeps the v2.13.0 cartridge/compiler architecture intact and addresses the two failures from the first 247-test local run (245 passed, 2 failed).

### Physical MMC1 CPU transaction timing

- `Mmc1Cartridge.CpuM2` is now falling-edge activated.
- Physical CPU reads and writes are completed on the falling M2 connector edge, while address, R/W and CPU data still represent the active transaction.
- This is required by the existing RP2A0x package model: one chip reaction stages/publishes the rising M2 edge together with next-cycle bus outputs atomically. Sampling mapper writes on that rising edge could therefore observe the newly-started cycle and miss the write being completed.
- No mapper knowledge was added to the motherboard or compiler. The correction is entirely inside the MMC1 cartridge package's own electrical behavior.
- The compiled MMC1 target remains a separate replaceable cartridge unit; the fixed motherboard compilation is unchanged.

### Unsupported-mapper regression

- `Boot_host_rejects_unsupported_mapper_before_power_is_applied` now uses mapper 2.
- Mapper 1 is no longer an unsupported mapper because v2.13.0 intentionally added MMC1 hardware.

### Validation status

The user's first v2.13.0 run discovered 247 tests with 245 passing and the two failures above. This environment does not contain the .NET SDK, so v2.13.1 remains a release candidate until the normal development machine runs `dotnet test`. Expected total remains 247 tests.
