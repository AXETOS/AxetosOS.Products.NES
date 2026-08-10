# AxetosOS Products / NES v2.47.0

## Konami VRC4 physical cartridge family

This release adds replaceable Konami VRC4 cartridge hardware for mapper numbers 21, 23 and 25 without adding mapper semantics to the motherboard or generic whole-circuit compiler.

Implemented hardware:

- VRC4a / VRC4c mapper-21 address-line package variants;
- VRC4e / VRC4f mapper-23 address-line package variants;
- VRC4b / VRC4d mapper-25 address-line package variants;
- exact NES 2.0 submapper selection and cartridge-local legacy iNES address-line compatibility decoding;
- two switchable 8 KiB PRG banks, fixed final banks and PRG swap mode;
- eight independently banked 1 KiB CHR windows with low/high register-nibble circuitry;
- horizontal, vertical and both single-screen CIRAM routes;
- optional 8 KiB work RAM;
- reusable `KonamiVrcIrqCounter` hardware with 8-bit reload/counter, cycle mode, 341-dot prescaler, enable-after-ack and IRQ assertion state;
- raw physical and compiled physical completion-edge CPU-cycle clocking;
- mapper/IRQ runtime diagnostics, board metadata, synthetic smoke ROM and conformance coverage.

NES 2.0 mapper-23/25 submapper 3 identifies VRC2b/VRC2c hardware. Those variants are explicitly rejected by the VRC4 cartridge package rather than being approximated as VRC4. The separated IRQ component is intended for genuine shared reuse by VRC6 in the next mapper-family sweep.

The validated incoming baseline is v2.46.3 with 596/596 Release tests passing plus commercial Mapper-69 validation on Gimmick! (Japan).
