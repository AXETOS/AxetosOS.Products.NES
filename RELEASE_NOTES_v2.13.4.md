# AxetosOS Products / NES v2.13.4

## Cartridge RAM topology and deterministic MMC1 A/B diagnostics

v2.13.4 is a correctness/diagnostic sweep over validated v2.13.3. It preserves the compiled-motherboard/replaceable-cartridge boundary and does not add mapper or product semantics to the whole-circuit compiler.

### Cartridge memory now follows the image's physical description

`VirtualHardwareNesRomReader` now decodes the NES 2.0 volatile/nonvolatile PRG and CHR RAM capacity nibbles and carries those capacities in `VirtualHardwareNesRomImage`. A zero NES 2.0 shift explicitly means that memory device is absent. Legacy iNES retains its compatibility inference because that format cannot describe the hardware with the same precision.

MMC1 no longer installs a universal 8 KiB PRG-RAM window. With explicit NES 2.0 metadata, a zero-capacity cartridge has no compiled or physical $6000-$7FFF driver. A described 8 KiB RAM device remains cartridge-local and its chip-enable follows MMC1 register state. Other PRG-RAM capacities are rejected until their actual SxROM board wiring is modeled.

The generic compiler contract gains an optional component-owned target-select predicate. This only tells the compiler whether circuitry represented by that component facet is electrically selected at the current moment; the compiler does not interpret mapper numbers, MMC1 registers, cartridge roles or addresses. Dynamic targets are intentionally excluded from static route folding.

### Deterministic real-ROM comparison

DesktopHost now accepts `--stop-frame N`. Reference and compiled runs can therefore stop automatically at the same PPU frame. The final MMC1 diagnostic includes:

- control, CHR0, CHR1 and PRG registers;
- actual PRG-RAM capacity and current enable state;
- mapper-write, serial-commit, reset-write and ignored-consecutive-write counts;
- an FNV-1a hash over every mapper write's physical address/data pair;
- the last mapper write and PPU-read count.

This makes a real-ROM A/B mismatch observable at the cartridge boundary without teaching the board or compiler what the mapper means.

### Coverage

Three new tests cover NES 2.0 RAM-size decoding, explicit absence of MMC1 PRG RAM and dynamic mapper-local PRG-RAM chip-enable. Existing compiled/reference MMC1 tests also compare mapper-write hashes.

Expected total: **253 tests**. Local `dotnet test` remains the acceptance gate.
