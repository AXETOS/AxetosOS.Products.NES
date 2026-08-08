# AxetosOS Products / NES v2.13.0

## Replaceable cartridge hardware unit + MMC1 architectural proof

### Fixed motherboard compilation is now cartridge-independent

- `CompiledLabMotherboardExecutionPlan` compiles motherboard-owned components only.
- Replaceable `ICompiledExternalDevice` hardware is bound after fixed-board compilation and can be removed/replaced without recreating the motherboard plan.
- The fixed compilation receives a stable `CompilationId`; regression coverage verifies the same ID survives NROM -> empty slot -> MMC1 replacement.
- External hardware is excluded from fixed-unit bus-target, clock/reset, signal-sink and bit-projection discovery.
- Fixed targets whose enable/address circuitry depends on connector-driven outputs remain dynamic boundary targets and are resolved from physical topology at runtime.
- The generic compiler still contains zero mapper/product/board-address semantics.

### ROM owns cartridge/mapper construction

- Added `VirtualCartridgeHardwareFactory` as the sole mapper-metadata composition boundary.
- Mapper 0 constructs `NromCartridge`.
- Mapper 1 constructs `Mmc1Cartridge`.
- `SharedVirtualRomSlot` and the regional machine now operate on a generic replaceable cartridge hardware connector contract.
- Cartridge insertion/ejection physically adds/removes package pins from the selected motherboard nets.
- Famicom `--compiled-lab` compilation is requested before ROM loading, so the fixed board exists before mapper hardware is constructed.
- NTSC/PAL physical boards use the same replaceable cartridge construction/connector path; their high-performance compiled-lab board plans are not claimed by this release.

### MMC1 hardware unit

The initial MMC1 cartridge model includes:

- five-write LSB-first serial register loading and bit-7 reset behavior;
- control, CHR bank 0, CHR bank 1 and PRG bank registers;
- 16 KiB/32 KiB PRG banking modes;
- 4 KiB/8 KiB CHR banking modes;
- 8 KiB cartridge PRG RAM;
- CHR RAM when the ROM has no CHR ROM;
- cartridge-local CIRAM `/CE` and A10 mirroring circuitry;
- physical CPU/PPU connector behavior plus matching generic compiled bus/combinational facets.

Scope of the first MMC1 proof is the standard base <=256 KiB PRG model. Later SxROM variants, revision-specific PRG-RAM disable/banking, larger outer-bank wiring and consecutive-CPU-cycle serial-write suppression remain future cartridge-hardware refinements rather than motherboard/compiler special cases.

### Regression coverage

Added coverage for:

- ROM-side mapper 1 cartridge construction;
- unsupported mapper rejection after mapper 0/1 support;
- fixed motherboard compilation identity across NROM/MMC1 replacement;
- compiled MMC1 PRG bank switching versus the physical reference runtime;
- serial reset behavior;
- 4 KiB CHR bank selection;
- PRG RAM round trips;
- all four MMC1 CIRAM A10 mirroring modes.

Baseline was 233 tests. The source changes add an expected 14 test cases, for an expected total of 247. The execution container used to build this patch does not contain the .NET SDK, so this release candidate is intentionally **not marked locally validated**; run `dotnet test` on the normal development machine before keeper/commit status.
