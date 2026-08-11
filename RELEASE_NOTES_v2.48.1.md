# AxetosOS Products / NES v2.48.1

## VRC6 register-address compile hotfix

This hotfix resolves the v2.48.0 C# compile blocker in `KonamiVrc6Cartridge.WriteMapper`.

- Preserve the translated VRC6 mapper register as a 16-bit hardware address after applying the `$F003` register mask.
- This matches `KonamiVrc6Audio.WriteRegister(ushort, byte)` and the cartridge package address-width contract.
- No VRC6 banking, IRQ, audio, nametable, RAM, compiler or motherboard behavior changes.
- The expected conformance count remains 656 tests once the suite compiles.
