# AxetosOS.Products.NES v2.18.0

## Live MMC1 CHR output and end-of-read PPU sampling

v2.17.0 corrected the compiled CPU-side timing so MMC1 serial commits occur on falling M2 just like the physical package. Alien Syndrome still rendered the same deterministic artifacts, which confirmed that compiled/reference parity was no longer the relevant boundary: both paths shared a cartridge/PPU read-window model that could retain the CHR byte too early.

### Physical cartridge correction

- MMC1 register commits now immediately refresh mapper-controlled PPU outputs from the address currently present on the connector.
- If CHR `/RD` is already active, a CHR0/CHR1/control change re-drives AD0-AD7 from the newly selected character-ROM bank without waiting for a new ALE or `/RD` transition.
- Re-driving an existing read does not create another PPU read transaction or increment the cartridge read counter.
- Control changes also refresh CIRAM `/CE` and A10 from the current PPU address.

This models the cartridge as connected hardware: the mapper's CHR address outputs feed the character memory continuously rather than acting like a software bank lookup performed once when a PPU read starts.

### Compiled read-window correction

- RP2C02 compiled rendering reads now call `BeginRead` when the physical data phase starts and `CompleteRead` when the PPU samples the byte on the following phase.
- CPU `$2007` reads use the same split transaction instead of resolving external memory at read assertion.
- `CompiledBusTargetDescriptor` has a generic optional selected-read-begin observer so a component can clock package-local state at read assertion while its data output remains resolved at a later physical phase.
- MMC1 uses that generic facet to preserve its PPU-read diagnostics; the compiler contains no mapper/product semantics.

### Tests

Two new regressions are added:

- an active physical MMC1 CHR read must change from one selected 4 KiB bank to another immediately after a CHR1 commit even though ALE and `/RD` never transition;
- the compiled MMC1 PPU target must observe read assertion separately from data resolution.

The existing rendering-time compiled/reference MMC1 test now also requires exact RP2C02 diagnostic-state and CIRAM-state equality.

Validation target: **266 tests**. User-local `dotnet test` remains the acceptance gate.
