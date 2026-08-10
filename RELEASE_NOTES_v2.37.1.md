# AxetosOS Products / NES v2.37.1

## Camerica validation fixture hotfix

v2.37.1 corrects one Mapper-71 no-bus-conflict test fixture from v2.37.0. The cartridge implementation itself is unchanged.

The failing test intentionally places `$01` under the CPU write at logical `$C000` so a bus-conflicting cartridge would AND a CPU `$05` write down to `$01`. The v2.37.0 fixture accidentally wrote that `$01` marker into byte zero of every 16 KiB PRG bank. The Camerica hardware correctly latched `$05`, selected PRG bank 5, and then read back the fixture's overwritten `$01` from `$8000`; the test incorrectly expected the original bank-5 marker `$45`.

v2.37.1 places the conflict probe only at the actual `$C000` write site in the fixed-last PRG bank. The switchable bank marker bytes remain intact, so the same test independently proves both properties:

- the CPU write is not altered by a ROM bus conflict (`BankRegister == $05`, selected bank 5);
- the resulting `$8000` read comes from bank 5 (`$45`).

No mapper, CPU, PPU, motherboard, compiler, timing, or cartridge behavior changed. The expected Release suite remains **390 tests** pending local validation.
