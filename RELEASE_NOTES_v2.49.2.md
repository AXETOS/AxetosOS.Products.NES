# AxetosOS Products / NES v2.49.2

## Namco 163 whole-machine CHR-nametable fixture hotfix

v2.49.1 added a dedicated `ChrNametableReadCount` diagnostic and strengthened raw/compiled whole-machine parity coverage, but the existing parity program only programmed a CHR-backed nametable bank; it never actually caused the RP2C02 to read from that nametable window. The resulting `ChrNametableReadCount > 0` assertion therefore failed even though the production mapper path was correct.

This hotfix changes only the conformance fixture. After programming `$C800=$21`, the synthetic CPU program now uses the real `$2006/$2007` RP2C02 register interface to read from `$2400`, which is nametable slot 1 and therefore physically backed by N163 CHR-ROM bank `$21`. The second `$2007` access accounts for the RP2C02 read buffer while both raw and startup-compiled machines must report the same non-zero CHR-backed nametable read count.

No Namco 163 mapper, CIRAM routing, IRQ, audio, compiler, motherboard or runtime behavior is changed.

The .NET suite must be validated by the user with `dotnet test -c Release`; no local pass is claimed by this patch.
