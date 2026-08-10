# AxetosOS Products / NES v2.41.0

## Mapper 34 physical board split: BNROM and NINA-001/002

- Adds Mapper 34 as replaceable cartridge hardware while preserving the fact that the historical mapper number covers two unrelated physical board families.
- Resolves NES 2.0 submapper 1 to NINA-001/002 and submapper 2 to BNROM/I-IM. Legacy iNES and submapper-0 images use fitted CHR geometry: more than 8 KiB CHR ROM selects NINA-001; otherwise BNROM.
- BNROM models:
  - one switchable 32 KiB PRG-ROM window with up to four physically addressable banks;
  - the fitted low PRG-bank latch outputs only;
  - standard AND-style CPU/ROM bus conflicts;
  - one fixed 8 KiB CHR-ROM or 8 KiB CHR-RAM device;
  - optional explicit 8 KiB PRG RAM for documented extended boards;
  - fixed H/V CIRAM wiring and no IRQ.
- NINA-001/002 models:
  - one switchable 32 KiB PRG-ROM window over up to 64 KiB;
  - two independently switchable 4 KiB CHR-ROM windows over up to 64 KiB;
  - mandatory 8 KiB volatile PRG RAM at `$6000-$7FFF`;
  - `$7FFD`, `$7FFE`, and `$7FFF` register writes that simultaneously write the physically-overlapped PRG-RAM locations;
  - no CPU/ROM bus conflicts, fixed H/V CIRAM wiring and no IRQ.
- Adds compiled/raw physical parity coverage for both board families, register-over-RAM behavior, bus conflicts, fixed mirroring, CHR RAM/ROM, optional BNROM PRG RAM and variant resolution.
- Adds two synthetic Mapper-34 smoke ROMs so BNROM and NINA-001 are validated independently rather than through a hybrid behavior.
- Adds desktop shutdown diagnostics for both Mapper-34 board variants.
- No generic whole-circuit compiler or motherboard mapper-specific behavior is added.
- Expected Release suite: 476 tests, pending local validation.
