# AxetosOS Products / NES v2.50.2

## VRC7 CHR-RAM board completion

- Adds Mapper-85 CHR-RAM-only cartridge topology instead of rejecting images with no CHR ROM.
- The eight VRC7 1 KiB CHR bank outputs now address either banked CHR ROM or banked writable CHR RAM according to the loaded image.
- Raw physical PPU writes sample the cartridge PPU data pins only while CHR RAM and /WR are selected; ROM boards remain read-only.
- Startup-compiled execution exposes the same physical PPU write target only for CHR-RAM cartridges.
- CHR RAM size follows explicit NES 2.0 RAM metadata, with the conventional 8 KiB fallback only for legacy headers that omit CHR ROM.
- Mixed CHR ROM + CHR RAM remains rejected until a concrete board topology requires it.
- Desktop diagnostics now report CHR memory type/size and PPU write count.
- Adds direct banked-CHR-RAM write coverage plus raw/compiled whole-machine parity through RP2C02 $2007.
- No VRC7 PRG banking, CIRAM routing, IRQ, SRAM, FM synthesis, motherboard or generic compiler semantics are changed.
