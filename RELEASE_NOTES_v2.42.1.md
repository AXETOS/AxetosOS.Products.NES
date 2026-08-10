# AxetosOS Products / NES v2.42.1

## MMC2 validation compile hotfix

- Fixes the new `VirtualHardwareMmc2CartridgeTests` compile failure by importing `AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation`, where the repository's existing `DigitalSignalSource` physical test stimulus component is defined.
- The test continues to drive PPU address pins through physical board nets; no mapper behavior or test intent is weakened.
- No Mapper 9 / MMC2 cartridge implementation, compiler, motherboard, CPU, PPU, host, or sample-ROM behavior changes.
- Expected Release suite remains 494 tests, pending local validation.
