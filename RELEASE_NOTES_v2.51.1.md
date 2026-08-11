# AxetosOS Products / NES v2.51.1

## MMC5 conformance fixture hotfix

- Updates the two generic unsupported-mapper tests to use Mapper 6 now that Mapper 5/MMC5 is implemented.
- Corrects the MMC5 raw-vs-compiled whole-machine parity fixture to select the Famicom motherboard (`Japan`) that its compiled-lab assertion targets.
- No production hardware, mapper, compiler, motherboard, audio, IRQ, banking, ExRAM, or PPU behavior changes.
- Expected Release suite remains 759 tests.
