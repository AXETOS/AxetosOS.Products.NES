# AxetosOS Products / NES v2.24.3

## xUnit analyzer hotfix

- Replaces three `Assert.True(...EndsWith(...))` assertions in `VirtualHardwareMmc1CartridgeTests` with `Assert.EndsWith(...)`.
- Resolves xUnit2009 under the repository's warnings/analyzers-as-errors policy.
- No CPU, PPU, APU, motherboard, cartridge, mapper, compiler, or runtime hardware behavior changes from v2.24.2.
- Expected local test total remains 276.
