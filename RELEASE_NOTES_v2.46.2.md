# AxetosOS Products / NES v2.46.2

Mapper 69 test-fixture compile hotfix.

- Adds the missing `AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation` import to `VirtualHardwareSunsoftFme7CartridgeTests` so the existing `DigitalSignalSource` laboratory component resolves correctly.
- Production Mapper 69 / Sunsoft FME-7 / 5B code is unchanged.
- No mapper, PSG, IRQ, timing, banking, mirroring, motherboard, compiler, or cartridge-boundary behavior changes.
- Expected Release suite remains 596 tests.
