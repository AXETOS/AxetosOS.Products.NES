# AxetosOS Products / NES v2.40.1

## DxROM validation analyzer hotfix

- Fixes xUnit analyzer rule `xUnit2031` in `VirtualHardwareDxromCartridgeTests` by using `Assert.Single(collection, predicate)` directly instead of filtering with LINQ before `Assert.Single`.
- No Mapper 206 / DxROM cartridge hardware behavior changed.
- No CPU, PPU, motherboard, compiler, cartridge factory, sample ROM, or runtime behavior changed.
- Expected Release suite remains 452 tests, pending local validation.
