# AxetosOS Products / NES v2.45.0

## Mapper 18 / Jaleco SS88006

v2.45.0 adds Mapper 18 as replaceable physical Jaleco SS88006 cartridge hardware.

The cartridge package provides:

- three independently switchable 8 KiB PRG-ROM windows at `$8000-$DFFF` and the final 8 KiB bank fixed at `$E000-$FFFF`;
- split low/high-nibble programming for each PRG bank register;
- eight independently switchable 1 KiB CHR-ROM windows, also programmed through split-nibble registers;
- optional 8 KiB work RAM in `$6000-$7FFF` with the SS88006 two-bit read/write protection latch;
- horizontal, vertical and both single-screen CIRAM routes;
- a package-local CPU-cycle IRQ down-counter with selectable 4-, 8-, 12- or 16-bit masked counting;
- open-collector physical IRQ output;
- SS88006 `$F003` external sample-control/index output state for boards fitted with a separate uPD7755C/uPD7756C package.

Optional Jaleco ADPCM sample packages are deliberately not approximated with host-side sound or invented sample data. The mapper exposes the control state so a future board-local sample package can attach at the physical hardware boundary.

No NES/mapper-specific logic was added to the generic hardware compiler or motherboard.

## Validation

The validated v2.44.0 baseline is 542 Release tests. v2.45.0 adds **27 test cases**, taking the expected Release suite to **569 tests**. Coverage includes reset mapping, split-nibble PRG/CHR registers, fitted address-line masking, work-RAM protection, all four CIRAM routes, all four IRQ widths and mode priority, IRQ acknowledge/reload behavior, raw physical M2 clocks, dynamic open-collector IRQ output, external sample-control state, compiled/raw execution parity, invalid board geometry and factory composition.

A synthetic Mapper-18 sample ROM is included at:

`samples/axetos-jaleco-ss88006-irq.nes`
