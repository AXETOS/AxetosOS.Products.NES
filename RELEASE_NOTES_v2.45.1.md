# AxetosOS Products / NES v2.45.1

## Mapper 18 interface-contract hotfix

- Adds the required `PpuWriteCount` member to `JalecoSs88006Cartridge` so the new Mapper 18 cartridge satisfies `IReplaceableCartridgeHardware`.
- Resets the counter with the cartridge diagnostics state for consistency with the other cartridge implementations.
- SS88006 boards modeled here use CHR ROM, so PPU writes remain ignored and the counter remains zero.
- No Mapper 18 banking, WRAM, mirroring, IRQ, sample-control, compiler, motherboard, CPU, PPU, or host behavior changed.
