# AxetosOS.Products.NES v2.23.0

## Physical cartridge PPU-bus topology correction

The v2.22.0 Alien Syndrome trace showed internally consistent MMC1 CHR provenance rather than random bank corruption: all 544 background pattern fetches in each sprite-zero window matched exact MMC1 read-completion events. Frames held entirely on CHR1=$19 sourced the full window from physical 4 KiB bank 25; frames with an in-window CHR1 $19->$18 commit split reads deterministically between banks 25 and 24 at the exact commit raster.

That evidence led to source inspection of the physical cartridge boundary, where a real topology error was found. RP2C0x multiplexes low address and data on package AD0-AD7, but the console motherboard's SN74LS373 demultiplexes A0-A7 before the cartridge connector. The previous model incorrectly exposed raw PPU AD0-AD7 plus ALE to each replaceable cartridge and duplicated a private low-address latch inside NROM/MMC1.

v2.23.0 changes actual execution topology:

- Famicom, NTSC NES and PAL NES motherboards now expose distinct PPU data nets and SN74LS373-latched low-address nets at the cartridge boundary.
- `SharedVirtualRomSlot` attaches cartridge PPU A0-A7 to SN74LS373 Q0-Q7, A8-A13 to the RP2C0x high-address traces, and D0-D7 to the RP2C0x/CIRAM data traces.
- ALE remains motherboard-internal between RP2C0x and SN74LS373 and is no longer a cartridge pin.
- `IReplaceableCartridgeHardware`, NROM and MMC1 now expose a 14-bit `PpuAddress` bus plus independent 8-bit bidirectional `PpuData`.
- NROM/MMC1 physical PPU processing samples the demultiplexed address directly; their compiled bus descriptors use the same physical connector pins.
- MMC1 retains its mapper-local serial registers, CHR/PRG decode, CIRAM outputs, consecutive CPU-write filter and active-read CHR re-drive; no mapper semantics move into the motherboard/compiler.

Regression coverage verifies distinct PPU address/data nets, latch-Q-to-cartridge-A0 wiring, raw-AD-to-cartridge-D0 wiring, no cartridge ALE attachment, independent standalone MMC1 address/data buses, and active `/RD` CHR remapping without a synthetic cartridge-local ALE latch.

No claim is made that Alien Syndrome is visually fixed until the corrected build is run locally. Expected suite total remains **266**; `dotnet test` is the acceptance gate.
