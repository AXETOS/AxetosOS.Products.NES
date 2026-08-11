# AxetosOS Products / NES v2.49.1

## Namco 163 CHR-backed nametable diagnostics

Final Lap commercially exercised Namco 163 nametable bank registers with CHR-ROM pages in nametable slots, but v2.49.0 exposed only the aggregate CHR read count. That made the successful CHR-backed nametable path impossible to distinguish from ordinary pattern-table CHR traffic in shutdown diagnostics.

This hotfix adds a dedicated `ChrNametableReadCount` diagnostic that increments only when the Namco 163 itself drives CHR ROM for PPU addresses in nametable space. Both raw package execution and startup-compiled execution account for the same physical reads, and conformance coverage now requires exact raw/compiled parity for that counter.

No mapper banking, CIRAM routing, CPU-cycle IRQ, sound RAM, wavetable audio, compiler topology or motherboard behavior is changed.

The .NET suite must be validated by the user with `dotnet test -c Release`; no local pass is claimed by this patch.
